namespace ProjectBeacon.OpsAgent;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectBeacon.Application.Operations;

public sealed class DockerLogProxyClient(
    HttpClient httpClient,
    OpsAgentOptions options,
    TimeProvider timeProvider)
{
    private const string TimestampHeader = "X-Beacon-Ops-Timestamp";
    private const string NonceHeader = "X-Beacon-Ops-Nonce";
    private const string SignatureHeader = "X-Beacon-Ops-Signature";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<PlatformContainerLogResponse?> GetLogsAsync(
        PlatformLogTarget service,
        int tail,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(service))
            throw new ArgumentOutOfRangeException(nameof(service));
        if (tail is < 1 || tail > options.MaximumTail)
            throw new ArgumentOutOfRangeException(nameof(tail));

        var serviceName = service switch
        {
            PlatformLogTarget.Web => "web",
            PlatformLogTarget.Worker => "worker",
            PlatformLogTarget.Migrate => "migrate",
            _ => throw new ArgumentOutOfRangeException(nameof(service))
        };
        var relativePath = $"/internal/v1/logs/{serviceName}?tail={tail.ToString(CultureInfo.InvariantCulture)}";
        var requestUri = new Uri(options.DockerLogProxyBaseUrl, relativePath);
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var signature = PlatformLogRequestSignature.Create(
            options.SharedSecret, timestamp, nonce, HttpMethod.Get.Method, requestUri.PathAndQuery);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(SignatureHeader, signature);

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Docker log proxy returned HTTP {(int)response.StatusCode}.");

        return await response.Content.ReadFromJsonAsync<PlatformContainerLogResponse>(JsonOptions, ct);
    }
}
