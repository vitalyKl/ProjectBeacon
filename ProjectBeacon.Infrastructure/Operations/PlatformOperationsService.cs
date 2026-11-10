namespace ProjectBeacon.Infrastructure.Operations;

using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Operations;
using ProjectBeacon.Application.Persistence;

public sealed class PlatformOperationsService(IBeaconDbFactory dbFactory, TimeProvider timeProvider)
    : IPlatformOperationsService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);
    private const int MaximumInstances = 100;

    public async Task RecordHeartbeatAsync(
        PlatformServiceHeartbeatWrite heartbeat,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        heartbeat.Validate();

        var tenant = PlatformTenant();
        await using var db = dbFactory.CreateDbContext(tenant);
        await db.Database.OpenConnectionAsync(ct);

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO "PlatformServiceHeartbeats"
                ("ServiceName", "InstanceId", "Version", "EnvironmentName",
                 "StartedAtUtc", "HeartbeatAtUtc", "HealthStatus")
            VALUES
                (@service, @instance, @version, @environment, @startedAt, @heartbeatAt, @health)
            ON CONFLICT ("ServiceName", "InstanceId") DO UPDATE SET
                "Version" = EXCLUDED."Version",
                "EnvironmentName" = EXCLUDED."EnvironmentName",
                "HeartbeatAtUtc" = EXCLUDED."HeartbeatAtUtc",
                "HealthStatus" = EXCLUDED."HealthStatus";
            """;
        AddParameter(command, "@service", heartbeat.Service.ToString());
        AddParameter(command, "@instance", heartbeat.InstanceId);
        AddParameter(command, "@version", heartbeat.Version);
        AddParameter(command, "@environment", heartbeat.EnvironmentName);
        AddParameter(command, "@startedAt", heartbeat.StartedAtUtc.UtcDateTime);
        AddParameter(command, "@heartbeatAt", heartbeat.HeartbeatAtUtc.UtcDateTime);
        AddParameter(command, "@health", heartbeat.Health.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<PlatformOperationsSnapshot> GetSnapshotAsync(
        AdminActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.HasPermission(OperatorPermission.OperationsRead))
            throw new UnauthorizedAccessException("The operator does not have operations-read permission.");

        var observedAt = timeProvider.GetUtcNow();
        var tenant = PlatformTenant();
        await using var db = dbFactory.CreateDbContext(tenant);
        await db.Database.OpenConnectionAsync(ct);

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT "ServiceName", "InstanceId", "Version", "EnvironmentName",
                   "StartedAtUtc", "HeartbeatAtUtc", "HealthStatus"
            FROM "PlatformServiceHeartbeats"
            ORDER BY "ServiceName" ASC, "HeartbeatAtUtc" DESC
            LIMIT @limit;
            """;
        AddParameter(command, "@limit", MaximumInstances);

        var instances = new List<PlatformServiceInstance>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var serviceText = reader.GetString(0);
            var healthText = reader.GetString(6);
            if (!Enum.TryParse<PlatformServiceKind>(serviceText, ignoreCase: false, out var service)
                || !Enum.IsDefined(service)
                || !Enum.TryParse<PlatformServiceHealth>(healthText, ignoreCase: false, out var health)
                || !Enum.IsDefined(health))
                continue;

            var lastHeartbeat = ReadUtc(reader.GetValue(5));
            instances.Add(new PlatformServiceInstance(
                service,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadUtc(reader.GetValue(4)),
                lastHeartbeat,
                health,
                observedAt - lastHeartbeat > StaleAfter));
        }

        return new PlatformOperationsSnapshot(observedAt, instances);
    }

    private static TenantContext PlatformTenant()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        return tenant;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ReadUtc(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
        _ => throw new InvalidOperationException("Stored heartbeat timestamp has an unsupported type.")
    };
}
