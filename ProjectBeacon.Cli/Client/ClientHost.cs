namespace ProjectBeacon.Cli.Client;

using System.Net.Http.Headers;

public static class ClientHost
{
    public static string Fingerprint() =>
        $"{Environment.MachineName}:{Environment.UserName}:{Environment.OSVersion.Platform}";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var opts = ClientOptions.Parse(args);
        var store = ClientStore.Load(opts.StorePath);
        if (!string.IsNullOrWhiteSpace(opts.Url))
            store.Url = opts.Url.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(opts.Token))
            store.Token = opts.Token;

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
        if (ClientStore.NeedsWizard(store) && interactive)
        {
            var wizard = await ClientTui.RunWizardAsync(store, opts.StorePath, ct);
            if (wizard != 0)
                return wizard;
            store = ClientStore.Load(opts.StorePath);
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
            await using var llama = new ClientLlamaSwap();
            using var daemon = new WorkstationDaemon(
                http,
                llama,
                () => WorkstationSettings.Load(),
                interactive ? null : msg => Console.Error.WriteLine(msg));

            if (interactive)
                return await ClientTui.RunDashboardAsync(daemon, store, linked.Token, opts.StorePath);

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
