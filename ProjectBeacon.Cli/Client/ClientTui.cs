namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
/// <summary>
/// First-run wizard and live dashboard. D checks for daemon updates.
/// </summary>
public static class ClientTui
{
    public static bool IsInteractive =>
        AnsiConsole.Profile.Capabilities.Interactive
        && !Console.IsInputRedirected
        && !Console.IsOutputRedirected;

    public static async Task<int> RunWizardAsync(ClientStore store, string? storePath, CancellationToken ct)
    {
        AnsiConsole.Write(new FigletText("Beacon").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]Workstation client — first-run setup[/]");
        AnsiConsole.WriteLine();

        var url = await AskUrlAsync(store.Url, ct);
        if (url is null)
            return 2;
        store.Url = url;

        var auth = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("How do you want to connect?")
                .AddChoices("Sign in with password", "Paste device token"));

        if (auth.StartsWith("Paste", StringComparison.Ordinal))
        {
            var token = AnsiConsole.Prompt(
                new TextPrompt<string>("Device token [grey]bcd_…[/]:")
                    .PromptStyle("green")
                    .Secret());
            if (string.IsNullOrWhiteSpace(token))
                return 2;
            store.Token = token.Trim();
        }
        else
        {
            while (!ct.IsCancellationRequested)
            {
                var login = AnsiConsole.Ask("Login:", Environment.UserName);
                var password = AnsiConsole.Prompt(new TextPrompt<string>("Password:").Secret());
                var name = AnsiConsole.Ask("Device name:", Environment.MachineName);
                var result = await ClientEnrollment.EnrollAsync(store.Url, login, password, name, ct);
                if (result.Ok)
                {
                    store.Token = result.Token;
                    store.DeviceId = result.Id;
                    AnsiConsole.MarkupLine("[green]Enrolled.[/] Token stored in client.json (not printed).");
                    break;
                }
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Error ?? "enroll failed")}[/]");
                if (!AnsiConsole.Confirm("Try again?", true))
                    return 1;
            }
        }

        store.Save(storePath);
        EditSettings();
        await ShowProbeAsync(ct);
        AskAutostart();

        AnsiConsole.MarkupLine("[green]Setup complete.[/] The client will stay connected while this window is open.");
        if (AnsiConsole.Confirm("Open Beacon in the browser?", true))
            OpenBrowser(store.Url);
        return 0;
    }

    public static async Task<int> RunDashboardAsync(
        WorkstationDaemon daemon, ClientStore store, CancellationToken ct, string? storePath = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = daemon.RunAsync(linked.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                ConsoleKey? key;
                try
                {
                    key = await LiveUntilKeyAsync(daemon, run, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (key is null or ConsoleKey.Q or ConsoleKey.Escape)
                    break;
                if (await HandleKeyAsync(key.Value, daemon, store, storePath, linked.Token))
                    break;
            }
        }
        finally
        {
            linked.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
        return 0;
    }

    private static async Task<string?> AskUrlAsync(string? current, CancellationToken ct)
    {
        var fallback = string.IsNullOrWhiteSpace(current) ? "http://localhost:5083" : current.TrimEnd('/');
        while (!ct.IsCancellationRequested)
        {
            var url = AnsiConsole.Ask("Control plane URL:", fallback).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(url))
                return null;
            var (ok, message) = await ClientEnrollment.ProbeUrlAsync(url, ct);
            if (ok)
            {
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");
                return url;
            }
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            if (!AnsiConsole.Confirm("Try a different URL?", true))
                return null;
            fallback = url;
        }
        return null;
    }

    private static async Task EditConnectionAsync(ClientStore store, WorkstationDaemon daemon, string? storePath, CancellationToken ct)
    {
        var current = string.IsNullOrWhiteSpace(store.Url) ? "http://localhost:5083" : store.Url.TrimEnd('/');
        var url = AnsiConsole.Ask("Control plane URL:", current).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
            url = current;
        var urlChanged = !string.Equals(url, current, StringComparison.OrdinalIgnoreCase);

        if (urlChanged)
        {
            var (ok, message) = await ClientEnrollment.ProbeUrlAsync(url, ct);
            if (ok)
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");
            else if (!AnsiConsole.Confirm($"[red]{Markup.Escape(message)}[/] — save this URL anyway?", false))
            {
                url = current;
                urlChanged = false;
            }
        }

        var token = AnsiConsole.Prompt(
            new TextPrompt<string>("Device token [grey]bcd_… (empty to keep current)[/]:")
                .PromptStyle("green")
                .Secret()
                .AllowEmpty());
        var tokenChanged = !string.IsNullOrWhiteSpace(token);

        if (urlChanged || tokenChanged)
        {
            if (urlChanged)
                store.Url = url;
            if (tokenChanged)
                store.Token = token.Trim();
            store.Save(storePath);
            if (urlChanged)
                daemon.SetControlPlane(store.Url);
            if (tokenChanged)
                daemon.SetToken(store.Token);
            AnsiConsole.MarkupLine("[green]Connection saved.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]No changes.[/]");
        }

        if (tokenChanged)
        {
            var (ok, message) = await ClientEnrollment.ValidateTokenAsync(store.Url, store.Token, ct);
            AnsiConsole.MarkupLine(ok ? $"[green]{Markup.Escape(message)}[/]" : $"[yellow]{Markup.Escape(message)}[/]");
        }
    }

    private static void EditSettings()
    {
        var (settings, readError) = WorkstationSettings.TryRead();
        if (readError is not null)
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(readError)}[/]");
        settings.ProjectsRoot = PromptPath("Projects root", settings.ProjectsRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects"));
        settings.ModelsRoot = PromptPath("Models root", settings.ModelsRoot ?? WorkstationSettings.DefaultModelsRoot);
        settings.LlamaSwapBin = PromptOptional("llama-swap binary", settings.LlamaSwapBin);
        settings.LlamaCppBin = PromptOptional("llama-server binary", settings.LlamaCppBin);
        settings.OpencodeDataDir = PromptOptional("OpenCode history dir", settings.OpencodeDataDir);
        settings.BeaconPath = PromptOptional("beacon executable", settings.BeaconPath ?? Environment.ProcessPath);
        settings.LlamaSwapPort = AnsiConsole.Ask("llama-swap port:", settings.LlamaSwapPort > 0 ? settings.LlamaSwapPort : 8080);
        AnsiConsole.MarkupLine("[grey]Command execution policy applies to native agents on this workstation. Auto skips consent; it is not an OS sandbox.[/]");
        var currentPolicy = settings.CommandPolicy?.Trim().ToLowerInvariant() is "off" or "auto" or "ask"
            ? settings.CommandPolicy.Trim().ToLowerInvariant()
            : "ask";
        settings.CommandPolicy = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Agent command policy")
                .AddChoices("off", "ask", "auto")
                .HighlightStyle(new Style(Color.Cyan1)));
        AnsiConsole.MarkupLine($"[grey]Previous setting: {Markup.Escape(currentPolicy)}[/]");
        settings.Save();
        AnsiConsole.MarkupLine("[green]Saved workstation.json[/]");
    }

    private static async Task ShowProbeAsync(CancellationToken ct)
    {
        await Task.Yield();
        var json = WorkstationProbe.ProbeJson();
        AnsiConsole.Write(ProbeTable(json));
        using var doc = JsonDocument.Parse(json);
        var missing = new List<string>();
        foreach (var id in new[] { "git", "node", "docker" })
        {
            if (!doc.RootElement.TryGetProperty(id, out var el) || el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
                missing.Add(id);
        }
        if (missing.Count == 0)
            return;
        var pick = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Install missing tools via winget? [grey]space to toggle[/]")
                .NotRequired()
                .AddChoices(missing));
        foreach (var id in pick)
        {
            try
            {
                AnsiConsole.Status().Start($"Installing {id}…", _ => WorkstationSetup.Install($$"""{"id":"{{id}}"}"""));
                AnsiConsole.MarkupLine($"[green]Installed {Markup.Escape(id)}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(id)}: {Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    private static void AskAutostart()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var enabled = WindowsAutostart.IsEnabled();
        var next = AnsiConsole.Confirm(enabled ? "Keep Windows autostart?" : "Start Beacon client with Windows?", true);
        if (next && !enabled)
        {
            WindowsAutostart.Enable();
            AnsiConsole.MarkupLine("[green]Autostart enabled.[/]");
        }
        else if (!next && enabled)
        {
            WindowsAutostart.Disable();
            AnsiConsole.MarkupLine("[yellow]Autostart removed.[/]");
        }
    }

    private static async Task<bool> HandleKeyAsync(ConsoleKey key, WorkstationDaemon daemon, ClientStore store, string? storePath, CancellationToken ct)
    {
        var stopAfterUpdate = false;
        switch (key)
        {
            case ConsoleKey.S:
                await EditConnectionAsync(store, daemon, storePath, ct);
                EditSettings();
                await daemon.SyncLlamaNowAsync(ct);
                break;
            case ConsoleKey.P:
                await ShowProbeAsync(ct);
                AnsiConsole.MarkupLine("[grey]Press any key…[/]");
                Console.ReadKey(true);
                break;
            case ConsoleKey.R:
                await daemon.ReloadLlamaAsync(ct);
                AnsiConsole.MarkupLine("[green]llama-swap reload requested.[/]");
                await Task.Delay(400, ct);
                break;
            case ConsoleKey.U:
                try
                {
                    await daemon.UnloadLlamaAsync(ct);
                    AnsiConsole.MarkupLine("[green]llama-swap unload requested.[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                    AnsiConsole.MarkupLine("[grey]Press any key…[/]");
                    Console.ReadKey(true);
                }
                break;
            case ConsoleKey.A:
                AskAutostart();
                break;
            case ConsoleKey.L:
                ShowLog(daemon.Snapshot);
                break;
            case ConsoleKey.O:
                OpenBrowser(store.Url);
                break;
            case ConsoleKey.D:
                try
                {
                    var update = await ClientUpdater.CheckAsync(ct);
                    AnsiConsole.MarkupLine($"[grey]Client {Markup.Escape(update.CurrentVersion)} · latest {Markup.Escape(update.LatestVersion)}[/]");
                    if (!update.UpdateAvailable)
                    {
                        AnsiConsole.MarkupLine("[green]The client is up to date.[/]");
                        await Task.Delay(900, ct);
                        break;
                    }
                    if (AnsiConsole.Confirm($"Install client {Markup.Escape(update.LatestVersion)} and restart?", true))
                    {
                        ClientUpdater.UpdateInfo? scheduled = null;
                        await AnsiConsole.Progress().StartAsync(async context =>
                        {
                            var task = context.AddTask("Preparing update", maxValue: 100);
                            scheduled = await ClientUpdater.DownloadAndScheduleAsync((progress, _) =>
                            {
                                task.Description = Markup.Escape(progress.Message);
                                if (progress.Percent is { } percent)
                                    task.Value = Math.Clamp(percent, 0d, 100d);
                                return Task.CompletedTask;
                            }, ct);
                            if (scheduled.UpdateAvailable)
                                task.Value = 100;
                        });

                        if (scheduled?.UpdateAvailable == true)
                        {
                            AnsiConsole.MarkupLine("[green]Update staged. The updater helper will restart Beacon; see L (Logs) for the final result.[/]");
                            stopAfterUpdate = true;
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[green]The client is already up to date.[/]");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Update failed: {Markup.Escape(ex.Message)}[/]");
                    AnsiConsole.MarkupLine("[grey]Press any key…[/]");
                    Console.ReadKey(true);
                }
                break;
        }
        return stopAfterUpdate;
    }

    private static async Task<ConsoleKey?> LiveUntilKeyAsync(WorkstationDaemon daemon, Task daemonRun, CancellationToken ct)
    {
        ConsoleKey? pressed = null;
        await AnsiConsole.Live(BuildView(daemon.Snapshot))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                while (!ct.IsCancellationRequested && !daemonRun.IsCompleted)
                {
                    ctx.UpdateTarget(BuildView(daemon.Snapshot));
                    if (Console.KeyAvailable)
                    {
                        pressed = Console.ReadKey(true).Key;
                        return;
                    }
                    await Task.Delay(250, ct);
                }
            });
        return pressed;
    }

    private static IRenderable BuildView(DaemonStatus s)
    {
        var llama = s.Llama;
        var llamaText = llama is null
            ? "[grey]not started[/]"
            : llama.Healthy
                ? $"[green]healthy[/] {Markup.Escape(llama.LoadedModel ?? "idle")}"
                : $"[yellow]{Markup.Escape(llama.Error ?? "unavailable")}[/]";
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("Control plane", $"[bold]{Markup.Escape(s.Url)}[/]");
        grid.AddRow("Status", s.Connected ? "[green]online[/]" : "[red]offline[/]");
        grid.AddRow("Heartbeat", s.LastHeartbeatAt is null
            ? "[grey]waiting[/]"
            : $"{s.LastHeartbeatAt:HH:mm:ss} {(s.HeartbeatStatus is null ? "" : s.HeartbeatStatus.ToString())}");
        grid.AddRow("Command", s.LastCommand is null
            ? "[grey]none[/]"
            : $"{Markup.Escape(s.LastCommand)} {(s.LastCommandOk ? "[green]ok[/]" : "[red]failed[/]")} {s.LastCommandAt:HH:mm:ss}");
        grid.AddRow("llama-swap", llamaText);
        if (s.Host is { } host)
        {
            var cpu = host.CpuPercent is { } c ? $"CPU {c:0}%" : "CPU -";
            var ram = host.RamUsedBytes is { } && host.RamTotalBytes is { }
                ? $"RAM {host.RamUsedBytes / (1024 * 1024)}/{host.RamTotalBytes / (1024 * 1024)} MB"
                : "RAM -";
            var gpu = host.Gpu?.UtilizationPercent is { } g ? $"GPU {g:0}%" : "";
            grid.AddRow("Load", $"{cpu}  {ram}  {gpu}".Trim());
        }
        if (s.UpdateProgress is { } updateProgress)
        {
            var progressText = Markup.Escape(updateProgress.Message);
            if (updateProgress.Percent is { } percent)
            {
                var filled = Math.Clamp((int)Math.Round(percent / 5d), 0, 20);
                var bar = new string('█', filled) + new string('░', 20 - filled);
                progressText += $" [cyan]{bar}[/] {percent:0}%";
            }
            grid.AddRow("Client update", progressText);
        }
        if (!string.IsNullOrWhiteSpace(s.Error))
            grid.AddRow("Error", $"[red]{Markup.Escape(s.Error)}[/]");

        var help = new Markup(
            "[grey][[S]]ettings  [[P]]robe  [[R]]eload  [[U]]nload  [[A]]utostart  [[L]]og  [[O]]pen  [[D]]aemon update  [[Q]]uit[/]");
        return new Rows(
            new Panel(grid) { Header = new PanelHeader("Beacon client"), Border = BoxBorder.Rounded },
            help);
    }

    private static void ShowLog(DaemonStatus status)
    {
        var lines = ClientFileLog.ReadRecent(200);
        if (lines.Count == 0)
            lines = status.Log;
        var body = lines.Count == 0 ? "[grey]no log yet[/]" : Markup.Escape(string.Join('\n', lines));
        AnsiConsole.Write(new Panel(body)
        {
            Header = new PanelHeader($"Log — {ClientFileLog.LogPath}"),
            Border = BoxBorder.Rounded
        });
        AnsiConsole.MarkupLine("[grey]Press any key…[/]");
        Console.ReadKey(true);
    }

    private static Table ProbeTable(string json)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Tool").AddColumn("Path");
        using var doc = JsonDocument.Parse(json);
        foreach (var name in new[] { "git", "node", "docker", "dotnet", "opencode", "llamaSwap", "llamaServer" })
        {
            var value = doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
            table.AddRow(name, string.IsNullOrWhiteSpace(value) ? "[red]missing[/]" : Markup.Escape(value!));
        }
        return table;
    }

    private static string PromptPath(string label, string current) =>
        AnsiConsole.Ask($"{label}:", current);

    private static string? PromptOptional(string label, string? current)
    {
        var value = AnsiConsole.Prompt(
            new TextPrompt<string>($"{label} [grey](empty to skip)[/]:")
                .AllowEmpty()
                .DefaultValue(current ?? ""));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        }
    }
}
