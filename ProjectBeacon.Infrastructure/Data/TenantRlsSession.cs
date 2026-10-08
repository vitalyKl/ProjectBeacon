namespace ProjectBeacon.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;
/// <summary>
/// Sets the same Postgres tenant settings with set_config. No-ops when the provider is not Npgsql.
/// </summary>
public static class TenantRlsSession
{
    public static async Task ApplyAsync(BeaconDbContext db, Guid? projectId, Guid? orgId, bool unscoped)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return;

        await db.Database.OpenConnectionAsync();
        var pid = projectId?.ToString() ?? "";
        var oid = orgId?.ToString() ?? "";
        var uns = unscoped ? "true" : "false";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_project_id', {pid}, false), set_config('app.tenant_org_id', {oid}, false), set_config('app.tenant_unscoped', {uns}, false)");
    }
}
