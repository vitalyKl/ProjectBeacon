namespace ProjectBeacon.Worker.Services;

using Application.Devices;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Every 60 seconds, unscoped, applies the stale-command recovery rule
/// (<c>CommandRecovery</c>): expired Running commands on revoked, offline, or
/// restarted daemons are failed or re-queued per their delivery semantics.
/// </summary>
public sealed class StaleCommandRecoveryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StaleCommandRecoveryService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public StaleCommandRecoveryService(IServiceScopeFactory scopeFactory, ILogger<StaleCommandRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunRecoveryAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stale command recovery tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> RunRecoveryAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BeaconDbContext>>();
        await using var db = dbFactory.CreateDbContext();
        using (TenantScope.EnterUnscoped())
        {
            await TenantRlsSession.ApplyAsync(db, null, null, unscoped: true);
            var recovered = await CommandRecovery.RecoverAsync(db, DateTime.UtcNow, ct);
            if (recovered > 0)
                _logger.LogInformation("Recovered {Count} stale workstation commands", recovered);
            return recovered;
        }
    }
}
