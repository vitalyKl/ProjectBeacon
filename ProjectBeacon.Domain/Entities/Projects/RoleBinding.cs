namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class RoleBinding : Entity, IProjectScoped
{
    public RoleBinding() { }

    public PipelineRole Role { get; private set; }
    public Guid ModelBackendId { get; private set; }
    public Guid ProjectId { get; private set; }

    public LocalModelBackend ModelBackend { get; private set; } = null!;

    public static RoleBinding Create(PipelineRole role, Guid modelBackendId, Guid projectId)
    {
        var binding = Entity.New<RoleBinding>();
        binding.Role = role;
        binding.ModelBackendId = modelBackendId;
        binding.ProjectId = projectId;
        return binding;
    }

    public void ChangeBackend(Guid modelBackendId)
    {
        ModelBackendId = modelBackendId;
    }
}
