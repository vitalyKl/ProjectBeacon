namespace ProjectBeacon.Domain.Entities.Projects;

using System.Text.Json;
using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// Project-owned policy for task-branch landing. Defaults are deliberately conservative:
/// no autonomous target-branch merge and no automatic revert until an Owner/Admin opts in.
/// </summary>
public sealed class ProjectGitPolicy : Entity, IProjectScoped
{
    public const int AbsoluteMaxChangedFiles = 200;
    public const int AbsoluteMaxChangedLines = 20_000;
    public const int MaxProtectedPathPatterns = 50;
    public const int MaxProtectedPathPatternLength = 200;

    public ProjectGitPolicy() { }

    public Guid ProjectId { get; private set; }
    public GitLandingAutonomyLevel AutonomyLevel { get; private set; }
    public GitDirtyCheckoutPolicy DirtyCheckoutPolicy { get; private set; }
    public GitAutoRevertPolicy AutoRevertPolicy { get; private set; }
    public int MaxChangedFiles { get; private set; }
    public int MaxChangedLines { get; private set; }
    public string AdditionalProtectedPathsJson { get; private set; } = "[]";
    public int Revision { get; private set; } = 1;
    public Guid? UpdatedByUserId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public static ProjectGitPolicy CreateDefault(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("ProjectId is required.", nameof(projectId));

        var now = DateTime.UtcNow;
        var policy = Entity.New<ProjectGitPolicy>();
        policy.ProjectId = projectId;
        policy.AutonomyLevel = GitLandingAutonomyLevel.Manual;
        policy.DirtyCheckoutPolicy = GitDirtyCheckoutPolicy.Escalate;
        policy.AutoRevertPolicy = GitAutoRevertPolicy.Off;
        policy.MaxChangedFiles = AbsoluteMaxChangedFiles;
        policy.MaxChangedLines = AbsoluteMaxChangedLines;
        policy.AdditionalProtectedPathsJson = "[]";
        policy.Revision = 1;
        policy.CreatedAt = now;
        policy.UpdatedAt = now;
        return policy;
    }

    public IReadOnlyList<string> GetAdditionalProtectedPaths()
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(AdditionalProtectedPathsJson) ?? [];
        }
        catch (JsonException)
        {
            // A corrupt persisted policy fails closed: do not treat it as an empty restriction set.
            throw new InvalidOperationException("The project's additional protected path policy is malformed.");
        }
    }

    public void Update(
        int expectedRevision,
        GitLandingAutonomyLevel autonomyLevel,
        GitDirtyCheckoutPolicy dirtyCheckoutPolicy,
        GitAutoRevertPolicy autoRevertPolicy,
        int maxChangedFiles,
        int maxChangedLines,
        IReadOnlyCollection<string>? additionalProtectedPaths,
        Guid updatedByUserId)
    {
        if (expectedRevision != Revision)
            throw new InvalidOperationException("Project Git policy changed since it was loaded. Reload the policy and retry.");
        if (!Enum.IsDefined(autonomyLevel) || !Enum.IsDefined(dirtyCheckoutPolicy) || !Enum.IsDefined(autoRevertPolicy))
            throw new ArgumentOutOfRangeException(nameof(autonomyLevel), "Git policy contains an unsupported enum value.");
        if (maxChangedFiles is < 1 or > AbsoluteMaxChangedFiles)
            throw new ArgumentOutOfRangeException(nameof(maxChangedFiles), $"Changed file limit must be between 1 and {AbsoluteMaxChangedFiles}.");
        if (maxChangedLines is < 1 or > AbsoluteMaxChangedLines)
            throw new ArgumentOutOfRangeException(nameof(maxChangedLines), $"Changed line limit must be between 1 and {AbsoluteMaxChangedLines}.");
        if (updatedByUserId == Guid.Empty)
            throw new ArgumentException("UpdatedByUserId is required.", nameof(updatedByUserId));

        var paths = NormalizeProtectedPaths(additionalProtectedPaths);
        AutonomyLevel = autonomyLevel;
        DirtyCheckoutPolicy = dirtyCheckoutPolicy;
        AutoRevertPolicy = autoRevertPolicy;
        MaxChangedFiles = maxChangedFiles;
        MaxChangedLines = maxChangedLines;
        AdditionalProtectedPathsJson = JsonSerializer.Serialize(paths);
        UpdatedByUserId = updatedByUserId;
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    private static IReadOnlyList<string> NormalizeProtectedPaths(IReadOnlyCollection<string>? paths)
    {
        if (paths is null || paths.Count == 0)
            return [];

        if (paths.Count > MaxProtectedPathPatterns)
            throw new ArgumentOutOfRangeException(nameof(paths), $"At most {MaxProtectedPathPatterns} additional protected path patterns are allowed.");

        var normalized = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var path = raw?.Trim().Replace('\\', '/') ?? string.Empty;
            if (path.Length is < 1 or > MaxProtectedPathPatternLength
                || path.StartsWith('/', StringComparison.Ordinal)
                || path.Contains(':')
                || path.Contains('\\')
                || path.Contains('\0')
                || path.Any(char.IsControl)
                || path.Split('/').Any(segment => segment is "" or "." or "..")
                || path.Contains("***", StringComparison.Ordinal)
                || (path.Contains("**", StringComparison.Ordinal)
                    && !path.Contains("**/", StringComparison.Ordinal)))
                throw new ArgumentException("Protected paths must be relative project paths using supported * and ** wildcards.", nameof(paths));

            normalized.Add(path);
        }

        return normalized.ToArray();
    }
}
