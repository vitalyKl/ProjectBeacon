namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

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
        WorkstationDaemon daemon, ClientStore store, CancellationToken ct)
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
                    key = await LiveUntilKeyAsync(daemon, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (key is null or ConsoleKey.Q or ConsoleKey.Escape)
                    break;
                await HandleKeyAsync(key.Value, daemon, store, linked.Token);
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

    private static void EditSettings()
    {
        var settings = WorkstationSettings.Load();
        settings.ProjectsRoot = PromptPath("Projects root", settings.ProjectsRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "projects"));
        settings.ModelsRoot = PromptPath("Models root", settings.ModelsRoot ?? WorkstationSettings.DefaultModelsRoot);
        settings.LlamaSwapBin = PromptOptional("llama-swap binary", settings.LlamaSwapBin);
        settings.LlamaCppBin = PromptOptional("llama-server binary", settings.LlamaCppBin);
        settings.OpencodeDataDir = PromptOptional("OpenCode history dir", settings.OpencodeDataDir);
        settings.LlamaSwapPort = AnsiConsole.Ask("llama-swap port:", settings.LlamaSwapPort > 0 ? settings.LlamaSwapPort : 8080);
        settings.Save();
        AnsiConsole.MarkupLine("[green]Saved workstation.json[/]");
    }

    private static async Task ShowProbeAsync(CancellationToken ct)
    {
        await Task.Yield();
        var json = WorkstationActions.ProbeJson();
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
                AnsiConsole.Status().Start($"Installing {id}…", _ => WorkstationActions.Install($$"""{"id":"{{id}}"}"""));
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

    private static async Task HandleKeyAsync(ConsoleKey key, WorkstationDaemon daemon, ClientStore store, CancellationToken ct)
    {
        switch (key)
        {
            case ConsoleKey.S:
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
        }
    }

    private static async Task<ConsoleKey?> LiveUntilKeyAsync(WorkstationDaemon daemon, CancellationToken ct)
    {
        ConsoleKey? pressed = null;
        await AnsiConsole.Live(BuildView(daemon.Snapshot))
            .AutoClear(true)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                while (!ct.IsCancellationRequested)
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
        if (!string.IsNullOrWhiteSpace(s.Error))
            grid.AddRow("Error", $"[red]{Markup.Escape(s.Error)}[/]");

        var help = new Markup(
            "[grey][[S]]ettings  [[P]]robe  [[R]]eload  [[U]]nload  [[A]]utostart  [[L]]og  [[O]]pen  [[Q]]uit[/]");
        return new Rows(
            new Panel(grid) { Header = new PanelHeader("Beacon client"), Border = BoxBorder.Rounded },
            help);
    }

    private static void ShowLog(DaemonStatus status)
    {
        var lines = status.Log.Count == 0 ? "[grey]no log yet[/]" : Markup.Escape(string.Join('\n', status.Log));
        AnsiConsole.Write(new Panel(lines) { Header = new PanelHeader("Log"), Border = BoxBorder.Rounded });
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
