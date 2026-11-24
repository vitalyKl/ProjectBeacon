namespace ProjectBeacon.Domain.Entities.Roadmap;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Projects;

/// <summary>
/// Immutable proof of a Release Candidate after ReleasePrepare has landed on its target branch.
/// A pushed branch or an open PR is not a Candidate. CandidateSha is the observed target-branch
/// commit; PreparedCommitSha identifies the version-projection commit carrying Beacon-Release.
/// </summary>
public sealed class ReleaseCandidateSnapshot : Entity, IProjectScoped
{
    public ReleaseCandidateSnapshot() { }

    public Guid ProjectId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public Guid CandidateId { get; private set; }
    public Guid PreparedByDeviceId { get; private set; }
    public string Version { get; private set; } = string.Empty;
    public string TagName { get; private set; } = string.Empty;
    public string TargetBranch { get; private set; } = string.Empty;
    public string BaseSha { get; private set; } = string.Empty;
    public string PreparedCommitSha { get; private set; } = string.Empty;
    public string CandidateSha { get; private set; } = string.Empty;
    public string CandidateTreeSha { get; private set; } = string.Empty;
    public int VersionPolicyRevision { get; private set; }
    public int ReleaseRevision { get; private set; }
    public string ManifestSha256 { get; private set; } = string.Empty;
    public string SnapshotJson { get; private set; } = "{}";
    public DateTime PreparedAtUtc { get; private set; }

    public Project Project { get; private set; } = null!;
    public Release Release { get; private set; } = null!;

    public static ReleaseCandidateSnapshot Create(
        Guid projectId,
        Guid releaseId,
        Guid candidateId,
        Guid preparedByDeviceId,
        string version,
        string tagName,
        string targetBranch,
        string baseSha,
        string preparedCommitSha,
        string candidateSha,
        string candidateTreeSha,
        int versionPolicyRevision,
        int releaseRevision,
        string manifestSha256,
        string snapshotJson,
        DateTime preparedAtUtc)
    {
        if (projectId == Guid.Empty || releaseId == Guid.Empty
            || candidateId == Guid.Empty || preparedByDeviceId == Guid.Empty)
            throw new ArgumentException("Project, Release, Candidate and preparation workstation ids are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        if (!IsGitSha(baseSha) || !IsGitSha(preparedCommitSha)
            || !IsGitSha(candidateSha) || !IsGitSha(candidateTreeSha))
            throw new ArgumentException("Candidate snapshot requires valid Git object SHAs.");
        if (version.Length > 100 || tagName.Length > 200 || targetBranch.Length > 200)
            throw new ArgumentException("Candidate version, tag, or target branch exceeds its limit.");
        if (versionPolicyRevision < 1 || releaseRevision < 1)
            throw new ArgumentException("Candidate snapshot revisions must be positive.");
        if (manifestSha256.Length != 64 || !manifestSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Manifest SHA-256 must contain exactly 64 hexadecimal characters.");
        if (snapshotJson.Length > 1_000_000)
            throw new ArgumentException("Candidate snapshot JSON exceeds 1 MB.");
        if (preparedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Candidate preparation time must be UTC.", nameof(preparedAtUtc));

        using var document = System.Text.Json.JsonDocument.Parse(snapshotJson);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ArgumentException("Candidate snapshot must be a JSON object.", nameof(snapshotJson));

        var snapshot = Entity.New<ReleaseCandidateSnapshot>();
        snapshot.ProjectId = projectId;
        snapshot.ReleaseId = releaseId;
        snapshot.CandidateId = candidateId;
        snapshot.PreparedByDeviceId = preparedByDeviceId;
        snapshot.Version = version.Trim();
        snapshot.TagName = tagName.Trim();
        snapshot.TargetBranch = targetBranch.Trim();
        snapshot.BaseSha = baseSha.ToLowerInvariant();
        snapshot.PreparedCommitSha = preparedCommitSha.ToLowerInvariant();
        snapshot.CandidateSha = candidateSha.ToLowerInvariant();
        snapshot.CandidateTreeSha = candidateTreeSha.ToLowerInvariant();
        snapshot.VersionPolicyRevision = versionPolicyRevision;
        snapshot.ReleaseRevision = releaseRevision;
        snapshot.ManifestSha256 = manifestSha256.ToLowerInvariant();
        snapshot.SnapshotJson = snapshotJson;
        snapshot.PreparedAtUtc = preparedAtUtc;
        return snapshot;
    }

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
