using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ProjectBeacon.Infrastructure.Data;
/// <summary>
/// On each opened Postgres connection, copies TenantScope into app.tenant_project_id, app.tenant_org_id, and app.tenant_unscoped.
/// </summary>
public sealed class TenantRlsConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is NpgsqlConnection)
            Apply(connection);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (connection is NpgsqlConnection)
            Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT set_config('app.tenant_project_id', @project, false), set_config('app.tenant_org_id', @org, false), set_config('app.tenant_unscoped', @unscoped, false)";
        var project = command.CreateParameter();
        project.ParameterName = "project";
        project.Value = TenantScope.IsUnscoped ? "" : TenantScope.CurrentProjectId?.ToString() ?? "";
        command.Parameters.Add(project);
        var org = command.CreateParameter();
        org.ParameterName = "org";
        org.Value = TenantScope.IsUnscoped ? "" : TenantScope.CurrentOrgId?.ToString() ?? "";
        command.Parameters.Add(org);
        var unscoped = command.CreateParameter();
        unscoped.ParameterName = "unscoped";
        unscoped.Value = TenantScope.IsUnscoped ? "true" : "false";
        command.Parameters.Add(unscoped);
        command.ExecuteNonQuery();
    }
}
