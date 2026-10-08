namespace ProjectBeacon.Cli.Client.ModelSwapping;

using ProjectBeacon.Infrastructure.LlamaSwap;
/// <summary>
/// Estimates model memory in MiB from the gguf file size, or from context size divided by 1024 when the file is missing.
/// </summary>
public static class VramEstimate
{
    public static long FromSpec(LlamaSwapModelSpec spec)
    {
        var file = ModelFile(spec.LaunchCommand);
        if (file is not null && File.Exists(file))
        {
            var mb = new FileInfo(file).Length / (1024 * 1024);
            return mb > 0 ? mb : 1;
        }

        var context = spec.ContextSize > 0 ? spec.ContextSize : ContextFromCommand(spec.LaunchCommand);
        if (context <= 0)
            return 0;
        return Math.Max(1, context / 1024);
    }

    private static int ContextFromCommand(string? command)
    {
        var (_, args) = CommandLineSplit.Split(command);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token is "--ctx-size" or "--ctx_size" or "-c")
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var next))
                    return next;
            }
            else if (token.StartsWith("--ctx-size=", StringComparison.Ordinal)
                && int.TryParse(token["--ctx-size=".Length..], out var inline))
            {
                return inline;
            }
        }

        return 0;
    }

    public static string? ModelFile(string? command)
    {
        var (_, args) = CommandLineSplit.Split(command);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token is "-m" or "--model")
                return i + 1 < args.Length ? args[i + 1] : null;
            if (token.StartsWith("--model=", StringComparison.Ordinal))
                return token["--model=".Length..];
        }

        return null;
    }
}
