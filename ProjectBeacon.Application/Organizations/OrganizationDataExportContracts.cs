namespace ProjectBeacon.Application.Organizations;

using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Common;

/// <summary>
/// A portable, redacted organization archive. Authentication secrets, API-token hashes,
/// device credentials, provider/MCP credentials, local runtime paths and platform audit
/// internals are intentionally not part of this customer data contract.
/// </summary>
public sealed record OrganizationDataExport(
    byte[] Content,
    string FileName,
    DateTimeOffset ExportedAtUtc,
    int ProjectCount,
    int AttachmentCount);

public interface IOrganizationDataExportService
{
    /// <summary>Checks the caller's owner/admin membership without generating an archive.</summary>
    Task<bool> CanExportAsync(
        Guid orgId,
        ActorContext actor,
        CancellationToken ct = default);

    /// <summary>Exports the target organization only after verifying its human owner/admin membership.</summary>
    Task<Result<OrganizationDataExport>> ExportAsync(
        Guid orgId,
        ActorContext actor,
        CancellationToken ct = default);
}
