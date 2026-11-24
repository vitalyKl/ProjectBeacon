namespace ProjectBeacon.Domain.Entities.Tasks;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Projects;

/// <summary>
/// Immutable workstation-reported evidence that a task's exact reviewed GitHub PR was merged
/// and its merge commit was found in the fetched remote target branch.
/// </summary>
public sealed class RemoteLandingProof : Entity, IProjectScoped
{
    public RemoteLandingProof() { }

    public Guid ProjectId { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid ReviewRunId { get; private set; }
    public string GitHubRepository { get; private set; } = string.Empty;
    public int PullRequestNumber { get; private set; }
    public string PullRequestUrl { get; private set; } = string.Empty;
    public string SourceBranch { get; private set; } = string.Empty;
    public string TargetBranch { get; private set; } = string.Empty;
    public string SourceSha { get; private set; } = string.Empty;
    public string MergeCommitSha { get; private set; } = string.Empty;
    public string RemoteBaseSha { get; private set; } = string.Empty;
    public DateTime MergedAtUtc { get; private set; }
    public DateTime VerifiedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static RemoteLandingProof Create(
        Guid projectId,
        Guid taskId,
        Guid deviceId,
        Guid reviewRunId,
        string repository,
        int pullRequestNumber,
        string pullRequestUrl,
        string sourceBranch,
        string targetBranch,
        string sourceSha,
        string mergeCommitSha,
        string remoteBaseSha,
        DateTime mergedAtUtc,
        DateTime verifiedAtUtc)
    {
        if (projectId == Guid.Empty || taskId == Guid.Empty || deviceId == Guid.Empty || reviewRunId == Guid.Empty)
            throw new ArgumentException("Project, task, device, and review run identifiers are required.");
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 200
            || !IsGitHubRepository(repository))
            throw new ArgumentException("A validated github.com owner/repository is required.", nameof(repository));
        if (pullRequestNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(pullRequestNumber));
        if (!IsGitHubPullRequestUrl(pullRequestUrl, repository, pullRequestNumber))
            throw new ArgumentException("Pull request URL must identify the supplied GitHub repository and PR number.", nameof(pullRequestUrl));
        if (!string.Equals(sourceBranch, $"beacon/task/{taskId:N}", StringComparison.Ordinal))
            throw new ArgumentException("Source branch must be the deterministic branch assigned to this task.", nameof(sourceBranch));
        if (!ProjectVersionPolicyCatalog.IsSafeBranchName(targetBranch))
            throw new ArgumentException("Target branch is not a safe Git branch name.", nameof(targetBranch));
        if (!IsGitSha(sourceSha) || !IsGitSha(mergeCommitSha) || !IsGitSha(remoteBaseSha))
            throw new ArgumentException("Source, merge, and fetched remote base commits must be full Git SHAs.");
        if (mergedAtUtc == default || verifiedAtUtc == default || mergedAtUtc > verifiedAtUtc.AddMinutes(2))
            throw new ArgumentException("Valid GitHub merge and workstation verification timestamps are required.");

        var proof = Entity.New<RemoteLandingProof>();
        proof.ProjectId = projectId;
        proof.TaskId = taskId;
        proof.DeviceId = deviceId;
        proof.ReviewRunId = reviewRunId;
        proof.GitHubRepository = repository;
        proof.PullRequestNumber = pullRequestNumber;
        proof.PullRequestUrl = pullRequestUrl;
        proof.SourceBranch = sourceBranch;
        proof.TargetBranch = targetBranch;
        proof.SourceSha = sourceSha.ToLowerInvariant();
        proof.MergeCommitSha = mergeCommitSha.ToLowerInvariant();
        proof.RemoteBaseSha = remoteBaseSha.ToLowerInvariant();
        proof.MergedAtUtc = mergedAtUtc.ToUniversalTime();
        proof.VerifiedAtUtc = verifiedAtUtc.ToUniversalTime();
        proof.CreatedAtUtc = DateTime.UtcNow;
        return proof;
    }

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool IsGitHubRepository(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2
            && parts.All(part => part.Length is > 0 and <= 100
                && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    private static bool IsGitHubPullRequestUrl(string value, string repository, int number)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        var expected = $"/{repository}/pull/{number}";
        return string.Equals(uri.AbsolutePath.TrimEnd('/'), expected, StringComparison.Ordinal);
    }
}
