namespace ProjectBeacon.OpsAgent;

using System.Text.RegularExpressions;
using ProjectBeacon.Application.Operations;

public sealed class OpsAgentOptions
{
    private static readonly Regex ComposeProjectPattern = new(
        "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private OpsAgentOptions(
        string dockerLogRoot,
        string composeProject,
        byte[] sharedSecret)
    {
        DockerLogRoot = dockerLogRoot;
        ComposeProject = composeProject;
        SharedSecret = sharedSecret;
    }

    public string DockerLogRoot { get; }
    public string ComposeProject { get; }
    public byte[] SharedSecret { get; }
    public int MaximumTail => 500;
    public int MaximumContainers => 2;
    public long MaximumLogFileBytes => 10L * 1024 * 1024;

    public static OpsAgentOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var root = configuration["OPS_AGENT_LOG_ROOT"] ?? "/host-docker-logs";
        var project = configuration["OPS_AGENT_COMPOSE_PROJECT"] ?? "beacon-alpha";
        var secretText = configuration["OPS_AGENT_SHARED_SECRET"];

        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("OPS_AGENT_LOG_ROOT must be an absolute path.");
        if (!ComposeProjectPattern.IsMatch(project))
            throw new InvalidOperationException("OPS_AGENT_COMPOSE_PROJECT is invalid.");
        if (string.IsNullOrWhiteSpace(secretText))
            throw new InvalidOperationException("OPS_AGENT_SHARED_SECRET must be configured.");

        byte[] secret;
        try
        {
            secret = Convert.FromHexString(secretText);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "OPS_AGENT_SHARED_SECRET must be a hexadecimal secret.",
                exception);
        }

        if (secret.Length < PlatformLogRequestSignature.MinimumSecretBytes)
            throw new InvalidOperationException("OPS_AGENT_SHARED_SECRET must contain at least 32 random bytes.");

        return new OpsAgentOptions(Path.GetFullPath(root), project, secret);
    }
}
