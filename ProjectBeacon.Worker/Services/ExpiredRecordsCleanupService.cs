namespace ProjectBeacon.Worker.Services;

using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class ExpiredRecordsCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExpiredRecordsCleanupService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    public ExpiredRecordsCleanupService(IServiceScopeFactory scopeFactory, ILogger<ExpiredRecordsCleanupService> logger)
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
                await RunCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task<int> RunCleanupAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BeaconDbContext>>();
        var total = 0;

        await using var db = dbFactory.CreateDbContext();
        using (TenantScope.EnterUnscoped())
        {
            await TenantRlsSession.ApplyAsync(db, null, null, unscoped: true);
            var now = DateTime.UtcNow;

            var expiredSessions = await db.Sessions
                .Where(s => s.IsActive && s.ExpiresAt != null && s.ExpiresAt < now)
                .ToListAsync(ct);
            foreach (var s in expiredSessions)
                s.Deactivate();
            total += expiredSessions.Count;
            if (expiredSessions.Count > 0)
                _logger.LogInformation("Deactivated {Count} expired user sessions", expiredSessions.Count);

            var expiredTokens = await db.ApiTokens
                .Where(t => t.ExpiresAt != null && t.ExpiresAt < now)
                .ToListAsync(ct);
            if (expiredTokens.Count > 0)
            {
                db.ApiTokens.RemoveRange(expiredTokens);
                total += expiredTokens.Count;
                _logger.LogInformation("Deleted {Count} expired API tokens", expiredTokens.Count);
            }

            var expiredResets = await db.PasswordResetTokens
                .Where(t => t.ExpiresAt < now)
                .ToListAsync(ct);
            if (expiredResets.Count > 0)
            {
                db.PasswordResetTokens.RemoveRange(expiredResets);
                total += expiredResets.Count;
                _logger.LogInformation("Deleted {Count} expired password-reset tokens", expiredResets.Count);
            }

            var expiredOrgInvites = await db.OrgInvites
                .Where(i => i.Status == InviteStatus.Pending && i.ExpiredAt < now)
                .ToListAsync(ct);
            foreach (var i in expiredOrgInvites)
                i.Expire();
            total += expiredOrgInvites.Count;
            if (expiredOrgInvites.Count > 0)
                _logger.LogInformation("Expired {Count} org invites", expiredOrgInvites.Count);

            var expiredProjectInvites = await db.ProjectInvites
                .Where(i => i.Status == InviteStatus.Pending && i.ExpiredAt < now)
                .ToListAsync(ct);
            foreach (var i in expiredProjectInvites)
                i.Expire();
            total += expiredProjectInvites.Count;
            if (expiredProjectInvites.Count > 0)
                _logger.LogInformation("Expired {Count} project invites", expiredProjectInvites.Count);

            await db.SaveChangesAsync(ct);
        }

        return total;
    }
}
