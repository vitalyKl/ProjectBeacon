namespace ProjectBeacon.Application.Tasks;

using System.Globalization;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.LlamaSwap;
using Microsoft.EntityFrameworkCore;

public sealed record SpawnedSession(Guid? ModelBackendId, string? LaunchSpec);

public interface ISessionSpawner
{
    Task<SpawnedSession> SpawnAsync(BeaconDbContext db, Guid projectId, PipelineRole role, Guid taskId, CancellationToken ct);
}

/// <summary>v1 spawner: fills LaunchSpec from the registry; the real spawn happens in the Phase 7 harness.</summary>
public sealed class ManualSessionSpawner : ISessionSpawner
{
    private readonly LlamaSwapOptions _options;

    public ManualSessionSpawner(LlamaSwapOptions options) => _options = options;

    public async Task<SpawnedSession> SpawnAsync(BeaconDbContext db, Guid projectId, PipelineRole role, Guid taskId, CancellationToken ct)
    {
        var phases = await db.TaskPhases.Where(p => p.TaskId == taskId).OrderBy(p => p.SortOrder).ToListAsync(ct);
        if (phases.Count > 0)
        {
            var phase = role switch
            {
                PipelineRole.Planner => phases.FirstOrDefault(p => p.Key == "understand") ?? phases[0],
                PipelineRole.Review => phases.FirstOrDefault(p => p.Key == "check") ?? phases[^1],
                _ => phases.FirstOrDefault(p => p.Key == "do") ?? (phases.Count > 1 ? phases[1] : phases[0])
            };
            if (phase.ModelBackendId is not Guid modelId)
                return new SpawnedSession(null, null);
            var chosen = await db.LocalModelBackends.FirstOrDefaultAsync(b => b.Id == modelId, ct);
            return chosen is null ? new SpawnedSession(null, null) : SessionFrom(chosen);
        }

        var binding = await db.RoleBindings
            .FirstOrDefaultAsync(b => b.Role == role && b.ProjectId == projectId, ct);
        if (binding is null)
            return new SpawnedSession(null, null);

        var backend = await db.LocalModelBackends.FirstOrDefaultAsync(b => b.Id == binding.ModelBackendId, ct);
        if (backend is null)
            return new SpawnedSession(null, null);

        return SessionFrom(backend);
    }

    private SpawnedSession SessionFrom(LocalModelBackend backend)
    {
        var port = (_options?.Port ?? 8080).ToString(CultureInfo.InvariantCulture);
        var command = backend.LaunchCommand.Replace("${PORT}", port, StringComparison.Ordinal);
        var spec = LlamaSwapConfigGenerator.BuildCommand(new LlamaSwapModelSpec(
            backend.Name, command, backend.ContextSize, backend.Ttl, backend.ExtraFlags));
        if (backend.BackendType == ModelBackendType.LlamaCpp
            && !spec.Contains("--no-reasoning-preserve", StringComparison.Ordinal))
            spec += " --no-reasoning-preserve";
        return new SpawnedSession(backend.Id, spec);
    }
}
