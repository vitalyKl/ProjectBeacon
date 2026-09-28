namespace ProjectBeacon.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;

public static class TenantRlsSession
{
    public static async Task ApplyAsync(BeaconDbContext db, Guid? projectId, Guid? orgId, bool unscoped)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return;

        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.tenant_project_id', @pid, false)," +
            "       set_config('app.tenant_org_id', @oid, false)," +
            "       set_config('app.tenant_unscoped', @uns, false)",
            projectId?.ToString() ?? "",
            orgId?.ToString() ?? "",
            unscoped ? "true" : "false");
    }
}
