namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Headers;
/// <summary>
/// Entry point for beacon client. Enroll stores the token in client.json and does not print it. Without a token, an interactive terminal opens the wizard. Otherwise the daemon runs headless.
/// </summary>
public static class ClientHost
{
    public static string Fingerprint() =>
        $"{Environment.MachineName}:{Environment.UserName}:{Environment.OSVersion.Platform}";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var opts = ClientOptions.Parse(args);
        var storePath = string.IsNullOrWhiteSpace(opts.StorePath) ? null : Path.GetFullPath(opts.StorePath);
        if (storePath is not null)
            Environment.SetEnvironmentVariable("BEACON_CLIENT_STORE", storePath);
        var store = ClientStore.Load(storePath);
        store.Url = (ClientConfiguration.ResolveValue(opts.Url, null, store.Url) ?? string.Empty).TrimEnd('/');
        store.Token = ClientConfiguration.ResolveValue(opts.Token, null, store.Token) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(opts.Url) || !string.IsNullOrWhiteSpace(opts.Token))
            store.Save(storePath);

        if (opts.Enroll)
        {
            if (string.IsNullOrWhiteSpace(store.Url) || string.IsNullOrWhiteSpace(opts.Login) || string.IsNullOrWhiteSpace(opts.Password))
            {
                Console.Error.WriteLine("beacon client enroll --url <api> --login <user> --password <pass>");
                return 2;
            }
            var enrolled = await ClientEnrollment.EnrollAsync(
                store.Url, opts.Login!, opts.Password!, opts.Name ?? Environment.MachineName, ct);
            if (!enrolled.Ok)
            {
                Console.Error.WriteLine(enrolled.Error ?? "enroll failed");
                return 1;
            }
            store.Token = enrolled.Token;
            store.DeviceId = enrolled.Id;
            store.Save(opts.StorePath);
            Console.Error.WriteLine($"Enrolled device {store.DeviceId}. Token stored in client.json (not printed).");
        }

        var interactive = !opts.Headless && ClientTui.IsInteractive;
        Environment.SetEnvironmentVariable("BEACON_CLIENT_RESTART_MODE", interactive ? "interactive" : "headless");
        if (ClientStore.NeedsWizard(store) && interactive)
        {
            var wizard = await ClientTui.RunWizardAsync(store, storePath, ct);
            if (wizard != 0)
                return wizard;
            store = ClientStore.Load(storePath);
        }

        if (ClientStore.NeedsWizard(store))
        {
            Console.Error.WriteLine("beacon client --url <api> --token <bcd_…>");
            Console.Error.WriteLine("Or: beacon client enroll --url <api> --login <user> --password <pass>");
            Console.Error.WriteLine("In a terminal, `beacon client` opens the first-run walkthrough.");
            return 2;
        }

        if (!ClientSingleton.TryEnter(out var mutex))
        {
            Console.Error.WriteLine("beacon client is already running.");
            return 3;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                linked.Cancel();
            };

            using var http = new HttpClient { BaseAddress = new Uri(store.Url.TrimEnd('/') + "/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.Token);
            await using var llama = new ClientLlamaSwap { VramChecker = new ModelSwapping.NvidiaSmiVramChecker() };
            await using var openCode = new ClientOpenCodeServe();
            var fileLog = new ClientFileLog();
            await using var daemon = new WorkstationDaemon(
                http,
                llama,
                () => WorkstationSettings.Load(),
                interactive ? fileLog.Write : msg => { fileLog.Write(msg); Console.Error.WriteLine(msg); },
                openCode);

            if (interactive)
                return await ClientTui.RunDashboardAsync(daemon, store, linked.Token, storePath);

            Console.Error.WriteLine($"beacon client connected to {store.Url}");
            await daemon.RunAsync(linked.Token);
            return 0;
        }
        finally
        {
            mutex?.Dispose();
        }
    }
}
