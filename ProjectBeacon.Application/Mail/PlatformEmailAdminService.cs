namespace ProjectBeacon.Application.Mail;

using System.Net.Mail;
using Application.Audit;
using Application.Admin;
using Application.Authorization;
using Application.Persistence;
using Application.Security;
using Domain.Entities.Audit;
using Domain.Entities.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

/// <summary>Safe view of platform SMTP configuration. The password is never returned.</summary>
public sealed record PlatformEmailSettingsDto(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string FromAddress,
    bool UseStartTls,
    bool HasPassword,
    string Source,
    DateTime? UpdatedAtUtc);

public sealed record SavePlatformEmailSettingsRequest(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string FromAddress,
    bool UseStartTls,
    string? Password,
    bool ClearPassword);

public sealed record EmailDeliveryLogDto(
    DateTime AtUtc,
    string Recipient,
    string Purpose,
    string Status,
    string? ErrorCode);

/// <summary>
/// Operator mail settings service. SMTP passwords are encrypted with the shared data-protection
/// key and are never included in a response or delivery log.
/// </summary>
public sealed class PlatformEmailAdminService
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector _secrets;
    private readonly IEmailSender _email;
    private readonly IConfiguration _configuration;
    private readonly IAuditWriter? _auditWriter;
    private readonly TimeProvider _time;

    public PlatformEmailAdminService(
        IBeaconDbFactory dbFactory,
        ISecretProtector secrets,
        IEmailSender email,
        IConfiguration configuration,
        IAuditWriter? auditWriter = null,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _email = email;
        _configuration = configuration;
        _auditWriter = auditWriter;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<PlatformEmailSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var tenant = GlobalTenant();
        await using var db = _dbFactory.CreateDbContext(tenant);
        var row = await db.PlatformEmailSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == PlatformEmailSettings.SingletonId, ct);

        if (row is not null)
        {
            return new PlatformEmailSettingsDto(
                row.Enabled,
                row.Host,
                row.Port,
                row.Username,
                row.FromAddress,
                row.UseStartTls,
                !string.IsNullOrEmpty(row.PasswordCipher),
                "database",
                row.UpdatedAt);
        }

        var host = Config("MAIL:Host");
        var from = Config("MAIL:From");
        var port = int.TryParse(Config("MAIL:Port"), out var configuredPort) ? configuredPort : 587;
        var configured = !string.IsNullOrWhiteSpace(host) || !string.IsNullOrWhiteSpace(from);

        return new PlatformEmailSettingsDto(
            !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(from),
            host,
            port,
            Config("MAIL:User"),
            from,
            port != 25 || !string.IsNullOrWhiteSpace(Config("MAIL:User")),
            !string.IsNullOrEmpty(Config("MAIL:Password")),
            configured ? "environment" : "not_configured",
            null);
    }

    public async Task<PlatformEmailSettingsDto> SaveAsync(
        SavePlatformEmailSettingsRequest request,
        ActorContext actor,
        CancellationToken ct = default)
    {
        await EnsureElevatedAsync(actor, ct);
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Host?.Trim() ?? string.Empty;
        var username = request.Username?.Trim() ?? string.Empty;
        var from = request.FromAddress?.Trim() ?? string.Empty;

        if (request.Port is < 1 or > 65535)
            throw new ArgumentException("SMTP port must be between 1 and 65535.");
        if (host.Length > 255 || username.Length > 320 || from.Length > 320)
            throw new ArgumentException("One or more SMTP fields exceed their maximum length.");
        if (request.Enabled && (host.Length == 0 || from.Length == 0))
            throw new ArgumentException("SMTP host and sender address are required when mail is enabled.");
        if (from.Length > 0)
        {
            try
            {
                _ = new MailAddress(from);
            }
            catch (FormatException)
            {
                throw new ArgumentException("Sender address is not a valid email address.");
            }
        }

        var tenant = GlobalTenant();
        await using var db = _dbFactory.CreateDbContext(tenant);
        var row = await db.PlatformEmailSettings
            .SingleOrDefaultAsync(item => item.Id == PlatformEmailSettings.SingletonId, ct);

        string? passwordCipher = null;
        if (request.ClearPassword)
        {
            passwordCipher = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(request.Password))
        {
            if (request.Password.Length > 2048)
                throw new ArgumentException("SMTP password is too long.");
            passwordCipher = _secrets.Seal(request.Password);
        }
        else if (row is null)
        {
            // Preserve an existing MAIL__Password while moving configuration into the Control Plane.
            var environmentPassword = Config("MAIL:Password");
            passwordCipher = string.IsNullOrEmpty(environmentPassword)
                ? string.Empty
                : _secrets.Seal(environmentPassword);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            row = PlatformEmailSettings.Create(
                request.Enabled,
                host,
                request.Port,
                username,
                passwordCipher ?? string.Empty,
                from,
                request.UseStartTls,
                now);
            db.PlatformEmailSettings.Add(row);
        }
        else
        {
            row.Update(
                request.Enabled,
                host,
                request.Port,
                username,
                passwordCipher,
                from,
                request.UseStartTls,
                now);
        }

        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            actor,
            "platform.email_settings.updated",
            "PlatformEmailSettings",
            PlatformEmailSettings.SingletonId.ToString(),
            null,
            null,
            outcome: AuditOutcome.Success,
            detailsJson: """{"resultCode":"settings_updated"}"""));

        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }

    public async Task TestConnectionAsync(ActorContext actor, CancellationToken ct = default)
    {
        await EnsureElevatedAsync(actor, ct);
        var settings = await GetAsync(ct);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Host))
            throw new InvalidOperationException("Configure and enable SMTP before testing the connection.");

        try
        {
            await _email.TestConnectionAsync(ct);
            await WriteTestAuditAsync(
                actor, "platform.email_connection_test.succeeded", "connection_ok", AuditOutcome.Success, ct);
        }
        catch
        {
            await WriteTestAuditAsync(
                actor, "platform.email_connection_test.failed", "connection_failed", AuditOutcome.Failed, ct);
            throw new InvalidOperationException("SMTP connection test failed. Check the host, port, TLS and credentials.");
        }
    }

    public async Task SendTestAsync(string recipient, ActorContext actor, CancellationToken ct = default)
    {
        await EnsureElevatedAsync(actor, ct);
        var address = recipient?.Trim() ?? string.Empty;
        if (address.Length is 0 or > 320)
            throw new ArgumentException("A valid recipient address is required.");
        try
        {
            _ = new MailAddress(address);
        }
        catch (FormatException)
        {
            throw new ArgumentException("A valid recipient address is required.");
        }

        var settings = await GetAsync(ct);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new InvalidOperationException("Configure and enable SMTP before sending a test message.");

        try
        {
            await _email.SendAsync(
                address,
                "ProjectBeacon SMTP test",
                "This is a test message from the ProjectBeacon Control Plane. SMTP delivery is working.",
                "smtp_test",
                ct);
            await WriteTestAuditAsync(actor, "platform.email_test.succeeded", "test_sent", AuditOutcome.Success, ct);
        }
        catch
        {
            await WriteTestAuditAsync(actor, "platform.email_test.failed", "smtp_test_failed", AuditOutcome.Failed, ct);
            throw new InvalidOperationException("SMTP test failed. Check the configuration and delivery log.");
        }
    }

    public async Task<IReadOnlyList<EmailDeliveryLogDto>> ListRecentAsync(CancellationToken ct = default)
    {
        var tenant = GlobalTenant();
        await using var db = _dbFactory.CreateDbContext(tenant);
        return await db.EmailDeliveryLogs
            .AsNoTracking()
            .OrderByDescending(item => item.At)
            .Take(50)
            .Select(item => new EmailDeliveryLogDto(
                item.At, item.Recipient, item.Purpose, item.Status, item.ErrorCode))
            .ToListAsync(ct);
    }

    private async Task WriteTestAuditAsync(
        ActorContext actor,
        string action,
        string resultCode,
        AuditOutcome outcome,
        CancellationToken ct)
    {
        if (_auditWriter is null)
            return;

        var tenant = GlobalTenant();
        await using var db = _dbFactory.CreateDbContext(tenant);
        _auditWriter.Stage(db, AuditEventFactory.ForActor(
            actor,
            action,
            "PlatformEmail",
            null,
            null,
            null,
            outcome: outcome,
            detailsJson: $$"""{"resultCode":"{{resultCode}}"}"""));
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureElevatedAsync(ActorContext actor, CancellationToken ct)
    {
        var access = await new DatabaseAdminGate(_dbFactory).RequireAsync(
            actor, OperatorPermission.EmailSettingsManage, requireElevation: true, ct);
        if (!access.Success)
            throw new UnauthorizedAccessException(access.Error ?? "Recent Control Plane authenticator verification is required.");
    }

    private string Config(string key) =>
        _configuration[key] ?? Environment.GetEnvironmentVariable(key.Replace(":", "__")) ?? string.Empty;

    private static TenantContext GlobalTenant()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        return tenant;
    }
}
