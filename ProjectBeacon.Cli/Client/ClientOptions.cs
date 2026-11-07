namespace ProjectBeacon.Cli.Client;
/// <summary>
/// Parsed beacon client arguments: URL, token, login, password, device name, store path, enroll, and headless.
/// </summary>
public sealed class ClientOptions
{
    public string? Url { get; init; }
    public string? Token { get; init; }
    public string? Login { get; init; }
    public string? Password { get; init; }
    public string? Name { get; init; }
    public string? StorePath { get; init; }
    public bool Enroll { get; init; }
    public bool Headless { get; init; }

    public static ClientOptions Parse(string[] args)
    {
        return new ClientOptions
        {
            Url = ClientConfiguration.ResolveValue(
                Arg(args, "--url"),
                Environment.GetEnvironmentVariable("BEACON_URL"),
                Environment.GetEnvironmentVariable("BEACON_API_URL")),
            Token = ClientConfiguration.ResolveValue(
                Arg(args, "--token"),
                Environment.GetEnvironmentVariable("BEACON_CLIENT_TOKEN"),
                Environment.GetEnvironmentVariable("BEACON_API_TOKEN")),
            Login = Arg(args, "--login"),
            Password = Arg(args, "--password"),
            Name = Arg(args, "--name"),
            StorePath = ClientConfiguration.ResolveValue(
                Arg(args, "--store"),
                Environment.GetEnvironmentVariable("BEACON_CLIENT_STORE")),
            Enroll = args.Length > 1 && string.Equals(args[1], "enroll", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(Arg(args, "--login")) && !string.IsNullOrWhiteSpace(Arg(args, "--password"))),
            Headless = Has(args, "--headless")
                || ClientConfiguration.IsEnabled(Environment.GetEnvironmentVariable("BEACON_CLIENT_HEADLESS"))
        };
    }

    public static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    public static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    public static string? EnvOrArg(string[] args, string name, string env) =>
        ClientConfiguration.ResolveValue(Arg(args, name), Environment.GetEnvironmentVariable(env));
}
