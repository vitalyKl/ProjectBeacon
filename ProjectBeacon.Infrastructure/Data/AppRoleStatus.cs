namespace ProjectBeacon.Infrastructure.Data;

/// <summary>
/// Result of the fail-closed role guard for the application's Postgres connection.
/// Diagnostics must never contain connection strings or passwords.
/// </summary>
public sealed record AppRoleStatus(
    bool Verified,
    string RoleName,
    bool Superuser,
    bool BypassRls,
    string? FailureReason);
