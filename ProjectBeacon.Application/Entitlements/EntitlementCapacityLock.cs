namespace ProjectBeacon.Application.Entitlements;

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
/// Serializes capacity-consuming mutations for one organization. PostgreSQL holds this
/// transaction-scoped advisory lock until commit/rollback; other providers rely on their
/// normal transaction behavior for tests and local development.
/// </summary>
public static class EntitlementCapacityLock
{
    public static async Task AcquireOrganizationAsync(
        DatabaseFacade database,
        IDbContextTransaction transaction,
        Guid orgId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        if (orgId == Guid.Empty)
            throw new ArgumentException("Organization id is required.", nameof(orgId));

        if (!string.Equals(
                database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
            return;

        await using var command = database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.DbType = DbType.String;
        parameter.Value = $"organization-capacity:{orgId:N}";
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(ct);
    }
}
