using ProjectBeacon.Cli;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("beacon mcp [--root <path>]");
    return 1;
}

if (!string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Unknown command. Use: beacon mcp");
    return 1;
}

var root = Environment.GetEnvironmentVariable("BEACON_PROJECT_ROOT");
for (var i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--root")
        root = args[i + 1];
}

root ??= Directory.GetCurrentDirectory();
return await McpStdioServer.RunAsync(root);
