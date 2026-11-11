namespace ProjectBeacon.Infrastructure.Operations;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Operations;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;

public sealed class HttpPlatformContainerLogClient(
    HttpClient httpClient,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<HttpPlatformContainerLogClient> logger,
    IBeaconDbFactory dbFactory,
    IAuditWriter auditWriter,
    IAdminGate adminGate) : IPlatformContainerLogClient
{
    private const int MaximumTail = 500;
    private const string TimestampHeader = "X-Beacon-Ops-Timestamp";
    private const string NonceHeader = "X-Beacon-Ops-Nonce";
    private const string SignatureHeader = "X-Beacon-Ops-Signature";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<Result<PlatformContainerLogResponse>> GetLogsAsync(
        AdminActor actor,
        PlatformLogTarget service,
        int tail = 200,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var operationsAccess = await adminGate.RequireAsync(actor, OperatorPermission.OperationsRead);
        var logAccess = await adminGate.RequireAsync(actor, OperatorPermission.ContainerLogsRead);
        if (!operationsAccess.Success
            || !logAccess.Success
            || logAccess.Value is null
            || logAccess.Value.UserId != actor.UserId
            || !logAccess.Value.HasPermission(OperatorPermission.OperationsRead)
            || !logAccess.Value.HasPermission(OperatorPermission.ContainerLogsRead))
            return Result.Failure<PlatformContainerLogResponse>(
                "Current OperationsRead and ContainerLogsRead permissions are required.", ErrorKind.Forbidden);
        var currentActor = logAccess.Value;
        if (!Enum.IsDefined(service))
            return Result.Failure<PlatformContainerLogResponse>(
                "A supported platform service is required.", ErrorKind.Validation);
        if (tail is < 1 or > MaximumTail)
            return Result.Failure<PlatformContainerLogResponse>(
                $"The log tail must be between 1 and {MaximumTail} lines.", ErrorKind.Validation);

        if (!Uri.TryCreate(configuration["OpsAgent:BaseUrl"], UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
            return Result.Failure<PlatformContainerLogResponse>(
                "The restricted operations log agent is not configured.", ErrorKind.Unavailable);

        byte[] secret;
        try
        {
            secret = Convert.FromHexString(configuration["OpsAgent:SharedSecret"] ?? string.Empty);
        }
        catch (FormatException)
        {
            secret = [];
        }

        if (secret.Length < PlatformLogRequestSignature.MinimumSecretBytes)
            return Result.Failure<PlatformContainerLogResponse>(
                "The restricted operations log agent secret is not configured.", ErrorKind.Unavailable);

        var serviceName = service switch
        {
            PlatformLogTarget.Web => "web",
            PlatformLogTarget.Worker => "worker",
            PlatformLogTarget.Migrate => "migrate",
            _ => throw new ArgumentOutOfRangeException(nameof(service))
        };
        var relativePath = $"/internal/v1/logs/{serviceName}?tail={tail.ToString(CultureInfo.InvariantCulture)}";
        var requestUri = new Uri(baseUri, relativePath);
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var signature = PlatformLogRequestSignature.Create(
            secret, timestamp, nonce, HttpMethod.Get.Method, requestUri.PathAndQuery);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(SignatureHeader, signature);

        try
        {
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden
                || response.StatusCode == HttpStatusCode.Unauthorized)
                return Result.Failure<PlatformContainerLogResponse>(
                    "The operations log agent rejected the authenticated request.", ErrorKind.Forbidden);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Result.Failure<PlatformContainerLogResponse>(
                    "No allowlisted container is available for this service.", ErrorKind.NotFound);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The operations log agent returned HTTP {StatusCode}.", (int)response.StatusCode);
                return Result.Failure<PlatformContainerLogResponse>(
                    "The operations log agent is unavailable.", ErrorKind.Unavailable);
            }

            var payload = await response.Content.ReadFromJsonAsync<PlatformContainerLogResponse>(
                JsonOptions, ct);
            if (payload is null)
                return Result.Failure<PlatformContainerLogResponse>(
                    "The operations log agent returned an empty response.", ErrorKind.Unavailable);

            var latestOperationsAccess = await adminGate.RequireAsync(
                currentActor, OperatorPermission.OperationsRead);
            var latestLogAccess = await adminGate.RequireAsync(
                currentActor, OperatorPermission.ContainerLogsRead);
            if (!latestOperationsAccess.Success
                || !latestLogAccess.Success
                || latestLogAccess.Value is null
                || latestLogAccess.Value.UserId != actor.UserId
                || !latestLogAccess.Value.HasPermission(OperatorPermission.OperationsRead)
                || !latestLogAccess.Value.HasPermission(OperatorPermission.ContainerLogsRead))
                return Result.Failure<PlatformContainerLogResponse>(
                    "Container log permissions changed while the request was running.", ErrorKind.Forbidden);

            try
            {
                await RecordAccessAsync(latestLogAccess.Value, service, payload.ReturnedLines, ct);
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Could not audit a container-log read for {Service}; withholding the log response.",
                    service);
                return Result.Failure<PlatformContainerLogResponse>(
                    "The container-log access could not be audited, so the response was withheld.",
                    ErrorKind.Unavailable);
            }

            return Result.Ok(payload);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(exception, "Could not retrieve restricted container logs for {Service}.", service);
            return Result.Failure<PlatformContainerLogResponse>(
                "The operations log agent could not be reached.", ErrorKind.Unavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
    private async Task RecordAccessAsync(
        AdminActor actor,
        PlatformLogTarget service,
        int returnedLines,
        CancellationToken ct)
    {
        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        var actorContext = new ActorContext(
            ActorType.Human,
            actor.UserId,
            IsAdmin: false,
            TokenId: null,
            DeviceId: null,
            ProjectId: null,
            OrgId: null,
            Capabilities: ApiTokenCapability.None);
        var details = JsonSerializer.Serialize(new
        {
            scope = "tail",
            count = returnedLines
        }, JsonOptions);
        auditWriter.Stage(db, AuditEventFactory.ForActor(
            actorContext,
            "platform.container_logs.read",
            "ContainerLogs",
            service.ToString(),
            null,
            null,
            detailsJson: details));
        await db.SaveChangesAsync(ct);
    }

}
