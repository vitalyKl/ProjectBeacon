using ProjectBeacon.Cli;
using ProjectBeacon.Cli.Client;

var original = args;
if (args.Length == 0)
    args = ["client"];

if (args[0] is "-h" or "--help")
{
    PrintUsage();
    return Hold(1, original);
}

try
{
    if (string.Equals(args[0], "client", StringComparison.OrdinalIgnoreCase))
        return Hold(await ClientHost.RunAsync(args), original);

    if (!string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Unknown command. Use: beacon mcp | beacon client");
        return Hold(1, original);
    }

    var root = Environment.GetEnvironmentVariable("BEACON_PROJECT_ROOT");
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--root")
            root = args[i + 1];
    }

    root ??= Directory.GetCurrentDirectory();
    return Hold(await McpStdioServer.RunAsync(root), original);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return Hold(1, original);
}

static void PrintUsage()
{
    Console.Error.WriteLine("beacon mcp [--root <path>]");
    Console.Error.WriteLine("beacon client [--url <api>] [--token <bcd_>] [--headless]");
    Console.Error.WriteLine("beacon client enroll --url <api> --login <user> --password <pass> [--name <device>]");
}

static int Hold(int code, string[] originalArgs)
{
    if (code == 0 || !ShouldHold(originalArgs))
        return code;
    Console.Error.WriteLine("Press any key to close.");
    try { Console.ReadKey(true); } catch { }
    return code;
}

static bool ShouldHold(string[] originalArgs)
{
    if (Console.IsInputRedirected || Console.IsOutputRedirected)
        return false;
    if (originalArgs.Length > 0 && string.Equals(originalArgs[0], "mcp", StringComparison.OrdinalIgnoreCase))
        return false;
    if (originalArgs.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase)))
        return false;
    return true;
}
