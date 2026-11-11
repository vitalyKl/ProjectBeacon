namespace ProjectBeacon.DockerLogProxy;

using System.Text.RegularExpressions;
using ProjectBeacon.Application.Operations;

public sealed class DockerLogProxyOptions
{
    private static readonly Regex ComposeProjectPattern = new(
        "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private DockerLogProxyOptions(string socketPath, string composeProject, byte[] sharedSecret)
    {
        DockerSocketPath = socketPath;
        ComposeProject = composeProject;
        SharedSecret = sharedSecret;
    }

    public string DockerSocketPath { get; }
    public string ComposeProject { get; }
    public byte[] SharedSecret { get; }
    public int MaximumTail => 500;
    public int MaximumContainers => 2;
    public int MaximumFrameBytes => 4 * 1024 * 1024;
    public int MaximumResponseBytes => 16 * 1024 * 1024;

    public static DockerLogProxyOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var socketPath = configuration["DOCKER_SOCKET_PATH"] ?? "/var/run/docker.sock";
        var composeProject = configuration["DOCKER_LOG_PROXY_COMPOSE_PROJECT"] ?? "beacon-alpha";
        var secretText = configuration["DOCKER_LOG_PROXY_SHARED_SECRET"];

        if (!Path.IsPathFullyQualified(socketPath))
            throw new InvalidOperationException("DOCKER_SOCKET_PATH must be an absolute path.");
        if (!ComposeProjectPattern.IsMatch(composeProject))
            throw new InvalidOperationException("DOCKER_LOG_PROXY_COMPOSE_PROJECT is invalid.");
        if (string.IsNullOrWhiteSpace(secretText))
            throw new InvalidOperationException("DOCKER_LOG_PROXY_SHARED_SECRET must be configured.");

        byte[] secret;
        try
        {
            secret = Convert.FromHexString(secretText);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "DOCKER_LOG_PROXY_SHARED_SECRET must be a hexadecimal secret.",
                exception);
        }

        if (secret.Length < PlatformLogRequestSignature.MinimumSecretBytes)
            throw new InvalidOperationException("DOCKER_LOG_PROXY_SHARED_SECRET must contain at least 32 random bytes.");

        return new DockerLogProxyOptions(Path.GetFullPath(socketPath), composeProject, secret);
    }
}
