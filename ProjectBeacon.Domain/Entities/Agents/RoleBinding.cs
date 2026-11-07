namespace ProjectBeacon.Domain.Entities.Agents;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>Which user-owned model backend a project's planner, actor, or review role uses.</summary>
public class RoleBinding : Entity, IProjectScoped
{
    public RoleBinding() { }

    public PipelineRole Role { get; private set; }
    public Guid ModelBackendId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ProfileInstructions { get; private set; } = string.Empty;

    public LocalModelBackend ModelBackend { get; private set; } = null!;

    public static RoleBinding Create(PipelineRole role, Guid modelBackendId, Guid projectId, string? profileInstructions = null)
    {
        var binding = Entity.New<RoleBinding>();
        binding.Role = role;
        binding.ModelBackendId = modelBackendId;
        binding.ProjectId = projectId;
        binding.ProfileInstructions = profileInstructions?.Trim() ?? string.Empty;
        return binding;
    }

    public void ChangeBackend(Guid modelBackendId)
    {
        ModelBackendId = modelBackendId;
    }

    public void ChangeProfileInstructions(string instructions)
    {
        ProfileInstructions = instructions.Trim();
    }
}
