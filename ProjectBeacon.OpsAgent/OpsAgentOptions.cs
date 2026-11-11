namespace ProjectBeacon.OpsAgent;

using ProjectBeacon.Application.Operations;

public sealed class OpsAgentOptions
{
    private OpsAgentOptions(Uri dockerLogProxyBaseUrl, byte[] sharedSecret)
    {
        DockerLogProxyBaseUrl = dockerLogProxyBaseUrl;
        SharedSecret = sharedSecret;
    }

    public Uri DockerLogProxyBaseUrl { get; }
    public byte[] SharedSecret { get; }
    public int MaximumTail => 500;

    public static OpsAgentOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var proxyText = configuration["OPS_AGENT_DOCKER_PROXY_URL"] ?? "http://docker-log-proxy:8081";
        var secretText = configuration["OPS_AGENT_SHARED_SECRET"];

        if (!Uri.TryCreate(proxyText, UriKind.Absolute, out var proxyUri)
            || proxyUri.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(proxyUri.UserInfo)
            || !string.IsNullOrEmpty(proxyUri.Query)
            || !string.IsNullOrEmpty(proxyUri.Fragment))
            throw new InvalidOperationException("OPS_AGENT_DOCKER_PROXY_URL must be an internal HTTP base URL.");

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

        return new OpsAgentOptions(proxyUri, secret);
    }
}
