namespace ProjectBeacon.Cli.Client;

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
            Url = EnvOrArg(args, "--url", "BEACON_URL"),
            Token = EnvOrArg(args, "--token", "BEACON_CLIENT_TOKEN"),
            Login = Arg(args, "--login"),
            Password = Arg(args, "--password"),
            Name = Arg(args, "--name"),
            StorePath = Arg(args, "--store"),
            Enroll = args.Length > 1 && string.Equals(args[1], "enroll", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(Arg(args, "--login")) && !string.IsNullOrWhiteSpace(Arg(args, "--password"))),
            Headless = Has(args, "--headless")
                || string.Equals(Environment.GetEnvironmentVariable("BEACON_CLIENT_HEADLESS"), "1", StringComparison.Ordinal)
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
        Arg(args, name) ?? Environment.GetEnvironmentVariable(env);
}
