namespace ProjectBeacon.Infrastructure;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProjectBeacon.Application.Operations;

public static class PlatformHeartbeatRegistration
{
    public static IServiceCollection AddPlatformHeartbeat(
        this IServiceCollection services,
        PlatformServiceKind service,
        string environmentName)
    {
        if (!Enum.IsDefined(service))
            throw new ArgumentOutOfRangeException(nameof(service));
        if (string.IsNullOrWhiteSpace(environmentName))
            throw new ArgumentException("Environment name is required.", nameof(environmentName));

        services.AddSingleton(new PlatformHeartbeatOptions(service, environmentName));
        services.AddHostedService<PlatformHeartbeatHostedService>();
        return services;
    }
}

public sealed record PlatformHeartbeatOptions(PlatformServiceKind Service, string EnvironmentName);

/// <summary>
/// Writes a metadata-only heartbeat every 30 seconds. The heartbeat is written through the
/// platform-only RLS scope; no public client or tenant actor can submit one.
/// </summary>
public sealed class PlatformHeartbeatHostedService(
    IServiceScopeFactory scopeFactory,
    PlatformHeartbeatOptions options,
    ILogger<PlatformHeartbeatHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly string _version = Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PublishAsync(PlatformServiceHealth.Healthy, stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await PublishAsync(PlatformServiceHealth.Healthy, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The final stopping heartbeat is attempted from StopAsync.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PublishAsync(PlatformServiceHealth.Stopping, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown must continue even when the final best-effort heartbeat is cancelled.
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }

    private async Task PublishAsync(PlatformServiceHealth health, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var operations = scope.ServiceProvider.GetRequiredService<IPlatformOperationsService>();
            await operations.RecordHeartbeatAsync(
                new PlatformServiceHeartbeatWrite(
                    options.Service,
                    _instanceId,
                    _version,
                    options.EnvironmentName,
                    _startedAtUtc,
                    DateTimeOffset.UtcNow,
                    health),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A provider can already be disposed while WebApplicationFactory or the host
            // is stopping (notably Windows EventLog). A best-effort heartbeat must never
            // turn a logging-sink failure into a host-disposal failure.
            try
            {
                logger.LogWarning(exception,
                    "Could not record the {Service} platform heartbeat for this instance.",
                    options.Service);
            }
            catch
            {
                // Logging during shutdown is best-effort too.
            }
        }
    }
}
