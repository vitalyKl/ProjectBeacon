namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;

public class ContextRevision : Entity, IProjectScoped
{
    public ContextRevision() { }

    public Guid ProjectId { get; private set; }
    public string CompiledHash { get; private set; } = string.Empty;
    public string CompilerVersion { get; private set; } = "1.0.0";
    public string? TargetRepoId { get; private set; }
    public string? TargetPath { get; private set; }
    public string? TargetTaskId { get; private set; }
    public string BriefMarkdown { get; private set; } = string.Empty;
    public string BriefJson { get; private set; } = string.Empty;
    public int TokenEstimate { get; private set; }
    public IList<Guid> SourceNodeIds { get; private set; } = [];
    public string? SessionId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public Project Project { get; private set; } = null!;

    public static ContextRevision Create(
        string compiledHash,
        string briefMarkdown,
        string briefJson,
        int tokenEstimate,
        Guid projectId,
        IList<Guid> sourceNodeIds,
        string? targetRepoId = null,
        string? targetPath = null,
        string? targetTaskId = null,
        string? sessionId = null)
    {
        var revision = Entity.New<ContextRevision>();
        revision.CompiledHash = compiledHash;
        revision.CompilerVersion = "1.0.0";
        revision.BriefMarkdown = briefMarkdown;
        revision.BriefJson = briefJson;
        revision.TokenEstimate = tokenEstimate;
        revision.ProjectId = projectId;
        revision.SourceNodeIds = new List<Guid>(sourceNodeIds);
        revision.TargetRepoId = targetRepoId;
        revision.TargetPath = targetPath;
        revision.TargetTaskId = targetTaskId;
        revision.SessionId = sessionId;
        revision.CreatedAt = DateTime.UtcNow;
        return revision;
    }
}
