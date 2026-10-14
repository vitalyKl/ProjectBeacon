namespace ProjectBeacon.Infrastructure.Data;

using System.Data.Common;
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
        Apply(db.Database.GetDbConnection(), projectId, orgId, unscoped);
    }

    /// <summary>
    /// Synchronous variant for the context factory, which cannot await.
    /// </summary>
    public static void Apply(BeaconDbContext db, Guid? projectId, Guid? orgId, bool unscoped)
    {
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
            return;

        db.Database.OpenConnection();
        Apply(db.Database.GetDbConnection(), projectId, orgId, unscoped);
    }

    private static void Apply(DbConnection? connection, Guid? projectId, Guid? orgId, bool unscoped)
    {
        if (connection is null)
            return;

        var pid = projectId?.ToString() ?? "";
        var oid = orgId?.ToString() ?? "";
        var uns = unscoped ? "true" : "false";
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT set_config('app.tenant_project_id', @project, false)," +
            "       set_config('app.tenant_org_id', @org, false)," +
            "       set_config('app.tenant_unscoped', @unscoped, false)";
        var pProject = command.CreateParameter();
        pProject.ParameterName = "@project";
        pProject.Value = pid;
        var pOrg = command.CreateParameter();
        pOrg.ParameterName = "@org";
        pOrg.Value = oid;
        var pUnscoped = command.CreateParameter();
        pUnscoped.ParameterName = "@unscoped";
        pUnscoped.Value = uns;
        command.Parameters.Add(pProject);
        command.Parameters.Add(pOrg);
        command.Parameters.Add(pUnscoped);
        command.ExecuteNonQuery();
    }
}
