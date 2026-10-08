namespace ProjectBeacon.Application.Devices;

using Application.Common;
using Domain.Entities.Devices;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Finds the project runtime for a device. Missing runtime is a failure, not an empty root.
/// </summary>
public static class ProjectRuntimeResolver
{
    public static async Task<Result<ProjectRuntime>> ResolveAsync(IBeaconDb db, Guid projectId, Guid deviceId, CancellationToken ct = default)
    {
        var runtime = await db.ProjectRuntimes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.DeviceId == deviceId, ct);
        if (runtime is null || string.IsNullOrWhiteSpace(runtime.LocalRoot))
            return Result.Failure<ProjectRuntime>(CommandSandbox.RuntimeRequired);
        return Result.Ok(runtime);
    }

    public static async Task<string?> ResolveRootAsync(IBeaconDb db, WorkstationCommand command, CancellationToken ct = default)
    {
        if (!CommandSandbox.IsProjectKind(command.Kind) || command.ProjectId is not { } projectId)
            return null;
        var resolved = await ResolveAsync(db, projectId, command.DeviceId, ct);
        return resolved.Success ? resolved.Value!.LocalRoot : null;
    }

    public static async Task<Dictionary<Guid, string>> ResolveRootsAsync(IBeaconDb db, Guid deviceId, IEnumerable<Guid> projectIds, CancellationToken ct = default)
    {
        var ids = projectIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();
        return await db.ProjectRuntimes.IgnoreQueryFilters()
            .Where(r => r.DeviceId == deviceId && ids.Contains(r.ProjectId))
            .ToDictionaryAsync(r => r.ProjectId, r => r.LocalRoot, ct);
    }
}
