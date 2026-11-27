namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Domain.Entities.Projects;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Trusted workstation executor for ReleasePrepare. It creates a dedicated release worktree,
/// updates only policy-configured version files and .beacon/release.json, commits the release
/// trailer, publishes a non-forced release branch and opens a PR. VerifyLanding later proves
/// inclusion in the remote target branch. This executor never creates tags or merges PRs.
/// </summary>
internal static class ReleasePrepareExecutor
{
    private const string MetadataSuffix = ".beacon-release.json";
    private const int MaxPayloadCharacters = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Metadata(
        int SchemaVersion, Guid ProjectId, Guid ReleaseId, string RepositoryRoot,
        string WorktreePath, string BranchName, string TargetBranch, string BaseSha,
        string? PreparedCommitSha, string? ManifestSha256);

    public static async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(
        string? localRoot, Guid projectId, string? payloadJson, CancellationToken ct)
    {
        if (projectId == Guid.Empty || string.IsNullOrWhiteSpace(localRoot) || !Directory.Exists(localRoot))
            return Fail("ReleasePrepare requires an existing project runtime root and project id.");
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson.Length > MaxPayloadCharacters)
            return Fail("ReleasePrepare payload is missing or exceeds the size limit.");

        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(payloadJson) as JsonObject ?? throw new JsonException("Payload must be an object.");
        }
        catch (JsonException exception)
        {
            return Fail("ReleasePrepare payload is invalid: " + Clip(exception.Message));
        }

        var action = Text(payload, "action");
        if (action is not ("Prepare" or "VerifyLanding"))
            return Fail("ReleasePrepare action must be Prepare or VerifyLanding.");
        var releaseId = GuidValue(payload, "releaseId");
        if (releaseId == Guid.Empty)
            return Fail("ReleasePrepare requires a valid releaseId.");

        try
        {
            var projectRoot = AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(localRoot));
            var repositoryValue = await GitValue(projectRoot, ct, [], "rev-parse", "--show-toplevel");
            if (repositoryValue.Error is not null)
                return Fail("The configured project runtime is not inside a Git repository. " + repositoryValue.Error);
            var repositoryRoot = AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(repositoryValue.Value!));
            if (!Within(repositoryRoot, projectRoot))
                return Fail("The project runtime root escapes its Git repository.");
            if (GitRepositoryOperationGate.HasActiveSessions(repositoryRoot))
                return Fail("ReleasePrepare is refused while an agent session is active in this repository.");

            using var repoGate = await GitRepositoryOperationGate.AcquireAsync(repositoryRoot, ct);
            if (GitRepositoryOperationGate.HasActiveSessions(repositoryRoot))
                return Fail("ReleasePrepare is refused while an agent session is active in this repository.");

            var filterResult = await Git(repositoryRoot, ct, [], "config", "--name-only", "--get-regexp", "^filter\\.");
            if (filterResult.ExitCode is not (0 or 1) || filterResult.TimedOut)
                return Fail("Could not inspect Git filter configuration safely.");
            var disabledFilters = AgentSessionWorktree.ParseFilterDriverNames(filterResult.StdOut);

            var prefix = await GitValue(projectRoot, ct, disabledFilters, "rev-parse", "--show-prefix");
            if (prefix.Error is not null)
                return Fail("Could not resolve the project directory inside its Git repository.");
            var projectPrefix = prefix.Value!.Trim().TrimEnd('/');
            var remote = await ReadOriginAsync(repositoryRoot, disabledFilters, ct);
            if (remote.Error is not null)
                return Fail(remote.Error);

            var targetBranch = Text(payload, "targetBranch");
            if (!ProjectVersionPolicyCatalog.IsSafeBranchName(targetBranch))
                return Fail("The configured release target branch is not a safe Git branch name.");
            var worktree = WorktreePath(repositoryRoot, projectId, releaseId);
            if (worktree.Error is not null)
                return Fail(worktree.Error);

            var ensured = await EnsureWorktreeAsync(
                repositoryRoot, worktree.Path!, projectId, releaseId, targetBranch,
                remote.Url!, disabledFilters, ct);
            if (ensured.Error is not null)
                return Fail(ensured.Error);

            if (action == "Prepare")
                return await PrepareAsync(
                    payload, repositoryRoot, projectPrefix, projectId, releaseId, targetBranch,
                    remote.Url!, remote.Repository!, disabledFilters, ensured.Value!, ct);

            return await VerifyLandingAsync(
                payload, repositoryRoot, projectPrefix, projectId, releaseId, targetBranch,
                remote.Url!, disabledFilters, ensured.Value!, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException or NotSupportedException
            or XmlException or XPathException or RegexMatchTimeoutException)
        {
            return Fail("ReleasePrepare failed safely (" + exception.GetType().Name + "): " + Clip(exception.Message));
        }
    }

    private static async Task<(bool Ok, string? Result, string? Error)> PrepareAsync(
        JsonObject payload, string repositoryRoot, string projectPrefix, Guid projectId, Guid releaseId,
        string targetBranch, string originUrl, string repository, string[] filters,
        Metadata metadata, CancellationToken ct)
    {
        var parsed = ParsePolicy(payload);
        if (parsed.Error is not null)
            return Fail(parsed.Error);
        var policy = parsed.Policy!;
        var requestedVersion = Text(payload, "version");
        if (!ProjectVersionPolicyCatalog.TryNormalizeVersion(policy, requestedVersion, out var version, out var versionError))
            return Fail(versionError ?? "Target version does not satisfy the configured policy.");
        var tagName = ProjectVersionPolicyCatalog.FormatTag(policy, version);
        var candidateId = GuidValue(payload, "candidateId");
        var versionPolicyRevision = PositiveInt(payload, "versionPolicyRevision");
        var releaseRevision = PositiveInt(payload, "releaseRevision");
        var snapshotJson = Text(payload, "snapshotJson");
        if (candidateId == Guid.Empty || versionPolicyRevision < 1 || releaseRevision < 1 || !IsJsonObject(snapshotJson))
            return Fail("ReleasePrepare requires candidateId, positive revisions and an immutable snapshot JSON object.");

        var worktree = metadata.WorktreePath;
        var dirty = await Git(worktree, ct, filters, "status", "--porcelain", "--untracked-files=all");
        if (!Succeeded(dirty))
            return GitFailure("Could not inspect the release worktree.", dirty.StdErr);
        if (!string.IsNullOrWhiteSpace(dirty.StdOut))
        {
            var existing = await ReadExistingPreparationAsync(worktree, filters, releaseId, candidateId, version, tagName, targetBranch,
                versionPolicyRevision, releaseRevision, snapshotJson, ct);
            if (existing.Error is not null || existing.CommitSha is null)
                return Fail("Release worktree contains uncommitted changes. " + (existing.Error ?? "No prepared commit can be resumed."));
            return await PublishBranchAsync(worktree, filters, originUrl, repository, metadata,
                projectId, releaseId, candidateId, version, tagName, targetBranch,
                versionPolicyRevision, releaseRevision, snapshotJson, existing.CommitSha, existing.ManifestSha!, ct);
        }

        if (metadata.PreparedCommitSha is not null)
        {
            var existing = await ReadExistingPreparationAsync(
                worktree, filters, releaseId, candidateId, version, tagName, targetBranch,
                versionPolicyRevision, releaseRevision, snapshotJson, ct);
            if (existing.Error is not null || existing.CommitSha is null)
                return Fail(existing.Error ?? "Saved ReleasePrepare commit could not be verified.");
            return await PublishBranchAsync(worktree, filters, originUrl, repository, metadata,
                projectId, releaseId, candidateId, version, tagName, targetBranch,
                versionPolicyRevision, releaseRevision, snapshotJson, existing.CommitSha, existing.ManifestSha!, ct);
        }

        var projectRoot = Path.GetFullPath(Path.Combine(worktree, projectPrefix));
        if (!Directory.Exists(projectRoot) || !Within(worktree, projectRoot))
            return Fail("Release worktree does not contain the configured project root.");

        var changedPaths = new HashSet<string>(StringComparer.Ordinal);
        var manifestSources = new JsonArray();
        foreach (var source in policy.Sources)
        {
            ct.ThrowIfCancellationRequested();
            var resolved = ResolveSourcePath(projectRoot, source.Path);
            if (resolved.Error is not null)
                return Fail(resolved.Error);
            var updated = await WriteVersionSourceAsync(resolved.Path!, source, policy, version, ct);
            if (!updated.Success)
                return Fail(updated.Error ?? "Could not update configured version source.");
            var repositoryPath = RepositoryRelative(projectPrefix, source.Path);
            changedPaths.Add(repositoryPath);
            manifestSources.Add(new JsonObject
            {
                ["path"] = repositoryPath,
                ["format"] = source.Format,
                ["selector"] = source.Selector
            });
        }

        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["projectId"] = projectId.ToString("D"),
            ["releaseId"] = releaseId.ToString("D"),
            ["candidateId"] = candidateId.ToString("D"),
            ["version"] = version,
            ["tagPattern"] = policy.TagPattern,
            ["tagName"] = tagName,
            ["targetBranch"] = targetBranch,
            ["versionPolicyRevision"] = versionPolicyRevision,
            ["releaseRevision"] = releaseRevision,
            ["policy"] = new JsonObject
            {
                ["scheme"] = policy.Scheme,
                ["componentCount"] = policy.ComponentCount,
                ["calendarFormat"] = policy.CalendarFormat,
                ["allowPrerelease"] = policy.AllowPrerelease
            },
            ["sources"] = manifestSources,
            ["snapshotJson"] = snapshotJson
        };
        var manifestPathResult = ResolveManifestPath(worktree);
        if (manifestPathResult.Error is not null)
            return Fail(manifestPathResult.Error);
        var manifestPath = manifestPathResult.Path!;
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions), new UTF8Encoding(false), ct);
        changedPaths.Add(".beacon/release.json");

        var staged = await Git(worktree, ct, filters,
            new[] { "add", "--" }.Concat(changedPaths.OrderBy(item => item, StringComparer.Ordinal)).ToArray());
        if (!Succeeded(staged))
            return GitFailure("Could not stage only the configured version sources and Beacon manifest.", staged.StdErr);
        var stagedPaths = await Git(worktree, ct, filters, "diff", "--cached", "--name-only", "-z");
        if (!Succeeded(stagedPaths))
            return GitFailure("Could not verify staged ReleasePrepare paths.", stagedPaths.StdErr);
        var actual = stagedPaths.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(changedPaths))
            return Fail("ReleasePrepare staged an unexpected set of files; no commit was created.");

        var commit = await Git(worktree, ct, filters,
            "-c", "user.name=ProjectBeacon",
            "-c", "user.email=release@projectbeacon.local",
            "commit", "-m", "chore(release): prepare " + version,
            "--trailer", "Beacon-Release: " + releaseId.ToString("D"));
        if (!Succeeded(commit))
            return GitFailure("Could not create the ReleasePrepare commit.", commit.StdErr);

        var preparedCommit = await GitValue(worktree, ct, filters, "rev-parse", "--verify", "HEAD");
        if (preparedCommit.Error is not null || !AgentTaskWorktree.IsGitSha(preparedCommit.Value!))
            return Fail("ReleasePrepare commit SHA could not be verified.");
        var trailer = await Git(worktree, ct, filters, "log", "-1", "--format=%(trailers:key=Beacon-Release,valueonly)");
        var trailerLines = trailer.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!Succeeded(trailer) || trailerLines.Length != 1
            || !string.Equals(trailerLines[0].Trim(), releaseId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return Fail("ReleasePrepare commit must contain exactly one matching Beacon-Release trailer.");

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        var manifestSha = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var newMetadata = metadata with { PreparedCommitSha = preparedCommit.Value, ManifestSha256 = manifestSha };
        await WriteMetadataAsync(worktree, newMetadata, ct);

        return await PublishBranchAsync(worktree, filters, originUrl, repository, newMetadata,
            projectId, releaseId, candidateId, version, tagName, targetBranch,
            versionPolicyRevision, releaseRevision, snapshotJson, preparedCommit.Value!, manifestSha, ct);
    }

    private static async Task<(bool Ok, string? Result, string? Error)> VerifyLandingAsync(
        JsonObject payload, string repositoryRoot, string projectPrefix, Guid projectId, Guid releaseId, string targetBranch,
        string originUrl, string[] filters, Metadata metadata, CancellationToken ct)
    {
        var parsed = ParsePolicy(payload);
        if (parsed.Error is not null)
            return Fail(parsed.Error);
        var policy = parsed.Policy!;
        var requestedVersion = Text(payload, "version");
        if (!ProjectVersionPolicyCatalog.TryNormalizeVersion(policy, requestedVersion, out var version, out var versionError))
            return Fail(versionError ?? "Target version does not satisfy the configured policy.");
        var tagName = ProjectVersionPolicyCatalog.FormatTag(policy, version);
        var candidateId = GuidValue(payload, "candidateId");
        var preparedCommit = Text(payload, "preparedCommitSha");
        var manifestSha = Text(payload, "manifestSha256");
        var versionPolicyRevision = PositiveInt(payload, "versionPolicyRevision");
        var releaseRevision = PositiveInt(payload, "releaseRevision");
        var snapshotJson = Text(payload, "snapshotJson");
        if (candidateId == Guid.Empty || !AgentTaskWorktree.IsGitSha(preparedCommit)
            || !Regex.IsMatch(manifestSha, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || versionPolicyRevision < 1 || releaseRevision < 1 || !IsJsonObject(snapshotJson))
            return Fail("VerifyLanding payload is incomplete or invalid.");
        if (metadata.PreparedCommitSha is null
            || !string.Equals(metadata.PreparedCommitSha, preparedCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(metadata.ManifestSha256, manifestSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(metadata.TargetBranch, targetBranch, StringComparison.Ordinal))
            return Fail("VerifyLanding does not match the persisted ReleasePrepare worktree metadata.");

        var fetch = await AgentTaskGitOperations.RunRemoteGitAsync(
            repositoryRoot, filters, ct, originUrl,
            "fetch", "--no-tags", "--no-recurse-submodules", "--upload-pack=git-upload-pack",
            "--", originUrl, "+refs/heads/" + targetBranch + ":refs/remotes/origin/" + targetBranch);
        if (!Succeeded(fetch))
            return GitFailure("Could not fetch the remote release target branch.", fetch.StdErr);
        var targetRef = "refs/remotes/origin/" + targetBranch;
        var candidateSha = await GitValue(repositoryRoot, ct, filters, "rev-parse", "--verify", targetRef);
        if (candidateSha.Error is not null || !AgentTaskWorktree.IsGitSha(candidateSha.Value!))
            return Fail("Remote target branch SHA could not be verified.");
        var ancestor = await Git(repositoryRoot, ct, filters, "merge-base", "--is-ancestor", preparedCommit, candidateSha.Value!);
        if (!Succeeded(ancestor))
            return Fail("ReleasePrepare commit is not contained in the remote target branch. This release PR must land with a merge-commit strategy that preserves the original Beacon-Release commit; squash and rebase rewrite that commit and are not supported. Configure the GitHub merge queue to use the merge method, then retry verification.");

        var candidateTree = await GitValue(repositoryRoot, ct, filters, "rev-parse", "--verify", candidateSha.Value + "^{tree}");
        if (candidateTree.Error is not null || !AgentTaskWorktree.IsGitSha(candidateTree.Value!))
            return Fail("Remote candidate tree SHA could not be verified.");

        var manifest = await Git(repositoryRoot, ct, filters, "show", candidateSha.Value + ":.beacon/release.json");
        if (!Succeeded(manifest))
            return GitFailure("Remote candidate does not contain .beacon/release.json.", manifest.StdErr);
        var actualManifestSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.StdOut))).ToLowerInvariant();
        if (!string.Equals(actualManifestSha, manifestSha, StringComparison.OrdinalIgnoreCase))
            return Fail("Remote manifest SHA-256 differs from the prepared release manifest.");

        JsonObject manifestJson;
        try
        {
            manifestJson = JsonNode.Parse(manifest.StdOut) as JsonObject ?? throw new JsonException("Manifest is not an object.");
        }
        catch (JsonException exception)
        {
            return Fail("Remote release manifest is invalid: " + Clip(exception.Message));
        }
        if (!SameGuid(Text(manifestJson, "projectId"), projectId)
            || !SameGuid(Text(manifestJson, "releaseId"), releaseId)
            || !SameGuid(Text(manifestJson, "candidateId"), candidateId)
            || Text(manifestJson, "version") != version
            || Text(manifestJson, "tagName") != tagName
            || Text(manifestJson, "targetBranch") != targetBranch
            || PositiveInt(manifestJson, "versionPolicyRevision") != versionPolicyRevision
            || PositiveInt(manifestJson, "releaseRevision") != releaseRevision
            || Text(manifestJson, "snapshotJson") != snapshotJson)
            return Fail("Remote release manifest does not describe this exact project/release/candidate snapshot.");

        foreach (var source in policy.Sources)
        {
            var repositoryPath = RepositoryRelative(projectPrefix, source.Path);
            var file = await Git(repositoryRoot, ct, filters, "show", candidateSha.Value + ":" + repositoryPath);
            if (!Succeeded(file))
                return GitFailure("Remote candidate is missing version source '" + repositoryPath + "'.", file.StdErr);
            if (!ProjectVersionPolicyCatalog.TryExtractVersion(policy, source, file.StdOut, out var projected, out var extractionError)
                || !string.Equals(projected, version, StringComparison.Ordinal))
                return Fail(extractionError ?? "Remote version source '" + repositoryPath + "' does not project the official version.");
        }

        return Success(new
        {
            action = "VerifyLanding",
            projectId,
            releaseId,
            candidateId,
            version,
            tagName,
            targetBranch,
            baseSha = metadata.BaseSha,
            preparedCommitSha = preparedCommit,
            candidateSha = candidateSha.Value,
            candidateTreeSha = candidateTree.Value,
            versionPolicyRevision,
            releaseRevision,
            manifestSha256 = manifestSha,
            snapshotJson,
            verifiedAtUtc = DateTime.UtcNow
        });
    }

    private static async Task<(bool Ok, string? Result, string? Error)> PublishBranchAsync(
        string worktree, string[] filters, string originUrl, string repository, Metadata metadata,
        Guid projectId, Guid releaseId, Guid candidateId, string version, string tagName, string targetBranch,
        int versionPolicyRevision, int releaseRevision, string snapshotJson, string preparedCommit, string manifestSha,
        CancellationToken ct)
    {
        var push = await AgentTaskGitOperations.RunRemoteGitAsync(
            worktree, filters, ct, originUrl, "push", "--no-follow-tags", "--porcelain",
            originUrl, preparedCommit + ":refs/heads/" + metadata.BranchName);
        if (!Succeeded(push))
            return GitFailure("Could not publish the release branch. Push is non-forced and no tag was created.", push.StdErr);

        var ghPath = WorkstationProbe.Which(
            "gh", NativeCommandRunner.BuildSafePath(Environment.GetEnvironmentVariable("PATH"), worktree));
        if (ghPath is null)
            return Fail("Release branch was pushed, but GitHub CLI (gh) is not installed.");
        var auth = await AgentTaskGitOperations.RunGitHubCliAsync(ghPath, worktree, ct, "auth", "status", "--hostname", "github.com");
        if (auth.ExitCode != 0 || auth.TimedOut)
            return Fail("Release branch was pushed, but GitHub CLI is not authenticated.");

        var owner = repository.Split('/')[0];
        var head = owner + ":" + metadata.BranchName;
        var list = await AgentTaskGitOperations.RunGitHubCliAsync(
            ghPath, worktree, ct, "pr", "list", "--repo", repository, "--head", head, "--state", "all",
            "--json", "number,url,baseRefName,state");
        if (list.ExitCode != 0 || list.TimedOut)
            return Fail("Release branch was pushed, but its PR state could not be inspected.");

        string? url = null;
        int? number = null;
        using (var document = JsonDocument.Parse(list.StdOut))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Fail("GitHub CLI returned an invalid release PR list.");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var baseName = item.TryGetProperty("baseRefName", out var baseElement) ? baseElement.GetString() : null;
                var state = item.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
                if (!string.Equals(baseName, targetBranch, StringComparison.Ordinal))
                    return Fail("Release branch already has a PR against another base; refusing to create a competing PR.");
                if (string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase))
                {
                    number = item.GetProperty("number").GetInt32();
                    url = item.GetProperty("url").GetString();
                    break;
                }
                if (string.Equals(state, "MERGED", StringComparison.OrdinalIgnoreCase))
                    return Fail("Release PR is already merged. Run VerifyLanding to verify the target branch.");
                if (string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase))
                    return Fail("Release PR is closed; reopen it explicitly before retrying.");
            }
        }

        if (number is null)
        {
            var body = "Prepared by ProjectBeacon Release Management." + Environment.NewLine + Environment.NewLine
                + "Release: " + releaseId.ToString("D") + Environment.NewLine
                + "Version: " + version + Environment.NewLine + "Tag: " + tagName + Environment.NewLine
                + "Prepared commit: " + preparedCommit + Environment.NewLine
                + "Version policy revision: " + versionPolicyRevision + Environment.NewLine
                + "Release revision: " + releaseRevision + Environment.NewLine + Environment.NewLine
                + "This PR contains only configured version projection files and .beacon/release.json." + Environment.NewLine
                + "Required merge method: create a merge commit. Do not squash or rebase this PR; ReleasePrepare must remain in target-branch ancestry." + Environment.NewLine
                + "For a merge queue, configure its Merge method to merge. Merging does not create a tag or deploy production; Beacon verifies the merged SHA before marking Candidate.";
            var created = await AgentTaskGitOperations.RunGitHubCliAsync(
                ghPath, worktree, ct, "pr", "create", "--repo", repository,
                "--head", head, "--base", targetBranch, "--title", "Release " + version, "--body", body);
            if (created.ExitCode != 0 || created.TimedOut)
                return Fail("Release branch was pushed, but GitHub did not create the PR: " + Clip(created.StdErr));
            url = created.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(url))
                return Fail("GitHub reported PR creation without returning its URL.");
        }

        return Success(new
        {
            action = "Prepare",
            projectId,
            releaseId,
            candidateId,
            version,
            tagName,
            targetBranch,
            releaseBranch = metadata.BranchName,
            baseSha = metadata.BaseSha,
            preparedCommitSha = preparedCommit,
            manifestSha256 = manifestSha,
            pullRequestNumber = number,
            pullRequestUrl = url,
            snapshotJson,
            publicationTagCreated = false
        });
    }

    private static async Task<(Metadata? Value, string? Error)> EnsureWorktreeAsync(
        string repositoryRoot, string worktreePath, Guid projectId, Guid releaseId, string targetBranch,
        string originUrl, string[] filters, CancellationToken ct)
    {
        var branch = "beacon/release/" + releaseId.ToString("N");
        var metadataPath = worktreePath + MetadataSuffix;
        var lockPath = worktreePath + ".lock";
        if (Reparse(repositoryRoot) || Reparse(worktreePath) || Reparse(metadataPath) || Reparse(lockPath))
            return (null, "Release worktree, metadata and lock paths cannot be symlinks or junctions.");
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        FileStream lease;
        try { lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return (null, "Another ReleasePrepare operation is using this release worktree."); }
        using (lease)
        {
            if (File.Exists(metadataPath))
            {
                Metadata? metadata;
                try { metadata = JsonSerializer.Deserialize<Metadata>(await File.ReadAllTextAsync(metadataPath, ct), JsonOptions); }
                catch (Exception exception) when (exception is IOException or JsonException)
                { return (null, "Existing release worktree metadata is invalid: " + Clip(exception.Message)); }

                if (metadata is null || metadata.SchemaVersion != 1 || metadata.ProjectId != projectId
                    || metadata.ReleaseId != releaseId || !SamePath(metadata.RepositoryRoot, repositoryRoot)
                    || !SamePath(metadata.WorktreePath, worktreePath) || metadata.BranchName != branch
                    || metadata.TargetBranch != targetBranch || !AgentTaskWorktree.IsGitSha(metadata.BaseSha))
                    return (null, "Existing release worktree metadata does not match this project and release.");

                var root = await GitValue(worktreePath, ct, filters, "rev-parse", "--show-toplevel");
                var currentBranch = await GitValue(worktreePath, ct, filters, "symbolic-ref", "--short", "HEAD");
                if (root.Error is not null || currentBranch.Error is not null
                    || !SamePath(root.Value!, worktreePath) || currentBranch.Value != branch)
                    return (null, "Existing release worktree is not checked out on its assigned release branch.");
                return (metadata, null);
            }

            if (Directory.Exists(worktreePath) || File.Exists(worktreePath))
                return (null, "A release worktree directory exists without Beacon metadata; refusing to reuse it.");
            var exists = await Git(repositoryRoot, ct, filters, "show-ref", "--verify", "--quiet", "refs/heads/" + branch);
            if (exists.ExitCode == 0)
                return (null, "Release branch exists without Beacon metadata; refusing to attach it implicitly.");
            if (exists.ExitCode != 1 || exists.TimedOut)
                return (null, "Could not inspect the release branch safely.");

            var fetch = await AgentTaskGitOperations.RunRemoteGitAsync(
                repositoryRoot, filters, ct, originUrl,
                "fetch", "--no-tags", "--no-recurse-submodules", "--upload-pack=git-upload-pack",
                "--", originUrl, "+refs/heads/" + targetBranch + ":refs/remotes/origin/" + targetBranch);
            if (!Succeeded(fetch))
                return (null, "Could not fetch release target branch: " + Clip(fetch.StdErr));
            var baseSha = await GitValue(repositoryRoot, ct, filters, "rev-parse", "--verify", "refs/remotes/origin/" + targetBranch);
            if (baseSha.Error is not null || !AgentTaskWorktree.IsGitSha(baseSha.Value!))
                return (null, "Remote release target branch SHA could not be verified.");

            var add = await Git(repositoryRoot, ct, filters, "worktree", "add", "-b", branch, worktreePath, baseSha.Value!);
            if (!Succeeded(add))
                return (null, "Could not create the isolated release worktree: " + Clip(add.StdErr));
            var created = new Metadata(1, projectId, releaseId, repositoryRoot, worktreePath,
                branch, targetBranch, baseSha.Value!, null, null);
            await WriteMetadataAsync(worktreePath, created, ct);
            return (created, null);
        }
    }

    private static async Task<(string? CommitSha, string? ManifestSha, string? Error)> ReadExistingPreparationAsync(
        string worktree, string[] filters, Guid releaseId, Guid candidateId, string version, string tagName,
        string targetBranch, int versionPolicyRevision, int releaseRevision, string snapshotJson, CancellationToken ct)
    {
        var head = await GitValue(worktree, ct, filters, "rev-parse", "--verify", "HEAD");
        if (head.Error is not null) return (null, null, head.Error);
        var status = await Git(worktree, ct, filters, "status", "--porcelain", "--untracked-files=all");
        if (!Succeeded(status) || !string.IsNullOrWhiteSpace(status.StdOut))
            return (null, null, "Release worktree is dirty.");
        var trailer = await Git(worktree, ct, filters, "log", "-1", "--format=%(trailers:key=Beacon-Release,valueonly)");
        var rows = trailer.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!Succeeded(trailer) || rows.Length != 1
            || !string.Equals(rows[0].Trim(), releaseId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return (null, null, "Release worktree HEAD does not have the expected Beacon-Release trailer.");
        var manifest = await Git(worktree, ct, filters, "show", "HEAD:.beacon/release.json");
        if (!Succeeded(manifest)) return (null, null, "Release manifest is missing from the prepared commit.");
        try
        {
            var json = JsonNode.Parse(manifest.StdOut) as JsonObject ?? throw new JsonException("Manifest is not an object.");
            if (Text(json, "version") != version || Text(json, "tagName") != tagName
                || Text(json, "targetBranch") != targetBranch
                || !SameGuid(Text(json, "releaseId"), releaseId)
                || !SameGuid(Text(json, "candidateId"), candidateId)
                || PositiveInt(json, "versionPolicyRevision") != versionPolicyRevision
                || PositiveInt(json, "releaseRevision") != releaseRevision
                || Text(json, "snapshotJson") != snapshotJson)
                return (null, null, "Existing release manifest differs from the requested Candidate snapshot, revisions, version, tag or target.");
        }
        catch (JsonException exception)
        {
            return (null, null, "Existing release manifest is invalid: " + Clip(exception.Message));
        }
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.StdOut))).ToLowerInvariant();
        return (head.Value, sha, null);
    }

    private static async Task<(string? Url, string? Repository, string? Error)> ReadOriginAsync(
        string root, string[] filters, CancellationToken ct)
    {
        var fetch = await Git(root, ct, filters, "remote", "get-url", "--all", "origin");
        if (!Succeeded(fetch)) return (null, null, "ReleasePrepare requires a configured GitHub origin.");
        var urls = fetch.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (urls.Length != 1 || !AgentTaskGitOperations.TryParseGitHubRemote(urls[0].Trim(), out var repository))
            return (null, null, "ReleasePrepare requires exactly one HTTPS or SSH github.com/owner/repository origin.");
        var push = await Git(root, ct, filters, "remote", "get-url", "--push", "--all", "origin");
        var pushUrls = push.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!Succeeded(push) || pushUrls.Length != 1
            || !AgentTaskGitOperations.TryParseGitHubRemote(pushUrls[0].Trim(), out var pushRepository)
            || !string.Equals(repository, pushRepository, StringComparison.OrdinalIgnoreCase))
            return (null, null, "Origin fetch and push URLs must be the same GitHub repository.");
        if (!AgentTaskWorktree.IsSupportedFetchRemote(urls[0].Trim()))
            return (null, null, "Configured Git origin uses an unsupported transport.");
        return (urls[0].Trim(), repository, null);
    }

    private static async Task<(bool Success, string? Error)> WriteVersionSourceAsync(
        string path, ProjectVersionSourcePolicy source, ProjectVersionPolicy policy, string version, CancellationToken ct)
    {
        if (Reparse(path)) return (false, "Version source is a symlink or junction: " + source.Path);
        var text = await File.ReadAllTextAsync(path, ct);
        if (text.Length > 2 * 1024 * 1024) return (false, "Version source exceeds 2 MB: " + source.Path);
        string updated;
        if (source.Format == "Xml")
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var element = document.XPathSelectElement(source.Selector);
            if (element is null) return (false, "XML selector did not match: " + source.Path);
            element.Value = version;
            var builder = new StringBuilder();
            using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = false, Indent = false }))
                document.Save(writer);
            updated = builder.ToString();
        }
        else if (source.Format == "Json")
        {
            var document = JsonNode.Parse(text);
            if (document is null || !SetJsonPointer(document, source.Selector, version))
                return (false, "JSON Pointer did not match: " + source.Path);
            updated = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        else if (source.Format == "Toml")
        {
            if (!SetToml(text, source.Selector, version, out updated))
                return (false, "TOML selector did not match: " + source.Path);
        }
        else if (source.Format == "Regex")
        {
            if (!SetRegex(text, source.Selector, version, out updated))
                return (false, "Regex selector did not match: " + source.Path);
        }
        else
            return (false, "Unsupported version source format: " + source.Format);

        if (!ProjectVersionPolicyCatalog.TryExtractVersion(policy, source, updated, out var projected, out var error)
            || projected != version)
            return (false, error ?? "Updated file does not project the requested version: " + source.Path);
        await File.WriteAllTextAsync(path, updated, new UTF8Encoding(false), ct);
        return (true, null);
    }

    private static bool SetJsonPointer(JsonNode root, string pointer, string value)
    {
        if (!pointer.StartsWith("/", StringComparison.Ordinal)) return false;
        var parts = pointer.Split('/').Skip(1)
            .Select(part => part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        if (parts.Length == 0) return false;
        JsonNode? current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current is JsonObject obj) current = obj[parts[i]];
            else if (current is JsonArray array && int.TryParse(parts[i], out var index) && index >= 0 && index < array.Count) current = array[index];
            else return false;
            if (current is null) return false;
        }
        var final = parts[^1];
        if (current is JsonObject parent && parent[final] is not null) { parent[final] = JsonValue.Create(value); return true; }
        if (current is JsonArray target && int.TryParse(final, out var idx) && idx >= 0 && idx < target.Count) { target[idx] = JsonValue.Create(value); return true; }
        return false;
    }

    private static bool SetToml(string text, string selector, string value, out string updated)
    {
        updated = text;
        var section = string.Empty;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var content = raw.Split('#', 2)[0].Trim();
            if (content.StartsWith("[", StringComparison.Ordinal) && content.EndsWith("]", StringComparison.Ordinal))
            { section = content[1..^1].Trim(); continue; }
            var equals = content.IndexOf('=');
            if (equals <= 0) continue;
            var key = content[..equals].Trim();
            var full = string.IsNullOrEmpty(section) ? key : section + "." + key;
            if (full != selector) continue;
            var rawEquals = raw.IndexOf('=');
            var commentIndex = raw.IndexOf('#');
            var comment = commentIndex >= 0 ? " " + raw[commentIndex..] : string.Empty;
            lines[i] = raw[..(rawEquals + 1)] + " \"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" + comment;
            updated = string.Join('\n', lines);
            return true;
        }
        return false;
    }

    private static bool SetRegex(string text, string selector, string value, out string updated)
    {
        updated = text;
        var match = Regex.Match(text, selector, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return false;
        var group = match.Groups["version"].Success ? match.Groups["version"]
            : match.Groups.Count > 1 ? match.Groups[1] : null;
        if (group is null || !group.Success) return false;
        updated = text[..group.Index] + value + text[(group.Index + group.Length)..];
        return true;
    }

    private static (string? Path, string? Error) ResolveSourcePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathRooted(relative)
            || relative.Contains(':') || relative.Split('/').Any(x => x is "" or "." or ".."))
            return (null, "Version source path is not canonical and project-relative: " + relative);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(root, full) || !File.Exists(full)) return (null, "Version source is missing or outside the project root: " + relative);
        var current = root;
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if (Reparse(current)) return (null, "Version source traverses a symlink or junction: " + relative);
        }
        return (full, null);
    }

    /// <summary>
    /// Resolves the Release manifest destination without following repository-controlled
    /// symlinks or junctions. The remote target branch is untrusted input at this boundary.
    /// </summary>
    internal static (string? Path, string? Error) ResolveManifestPath(string worktree)
    {
        if (string.IsNullOrWhiteSpace(worktree) || !Directory.Exists(worktree))
            return (null, "Release manifest requires an existing release worktree.");

        var fullWorktree = Path.GetFullPath(worktree);
        if (Reparse(fullWorktree))
            return (null, "Release worktree cannot be a symbolic link or junction.");
        var resolvedWorktree = AgentTaskWorktree.ResolvePhysicalPath(fullWorktree);
        if (!SamePath(fullWorktree, resolvedWorktree))
            return (null, "Release worktree path resolves outside its assigned directory.");

        var beaconDirectory = Path.Combine(fullWorktree, ".beacon");
        if (Reparse(beaconDirectory))
            return (null, "The .beacon manifest directory cannot be a symbolic link or junction.");
        if (File.Exists(beaconDirectory))
            return (null, "The .beacon manifest path exists but is not a directory.");

        Directory.CreateDirectory(beaconDirectory);
        if (Reparse(beaconDirectory))
            return (null, "The .beacon manifest directory changed to a symbolic link or junction.");
        var resolvedBeaconDirectory = AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(beaconDirectory));
        if (!Within(resolvedWorktree, resolvedBeaconDirectory)
            || !SamePath(beaconDirectory, resolvedBeaconDirectory))
            return (null, "The .beacon manifest directory must remain inside the release worktree.");

        var manifestPath = Path.Combine(resolvedBeaconDirectory, "release.json");
        if (Reparse(manifestPath))
            return (null, "The .beacon/release.json manifest cannot be a symbolic link or junction.");
        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!Within(resolvedWorktree, fullManifestPath))
            return (null, "The release manifest path escapes the release worktree.");

        return (fullManifestPath, null);
    }

    private static string RepositoryRelative(string prefix, string source) =>
        string.IsNullOrWhiteSpace(prefix) ? source : prefix + "/" + source;

    private static (ProjectVersionPolicy? Policy, string? Error) ParsePolicy(JsonObject payload)
    {
        var json = Text(payload, "versionPolicyJson");
        if (string.IsNullOrWhiteSpace(json)) return (null, "ReleasePrepare payload has no versionPolicyJson.");
        return ProjectVersionPolicyCatalog.TryRead(json, out var policy, out var error)
            ? (policy, null) : (null, error ?? "Version policy is invalid.");
    }

    private static (string? Path, string? Error) WorktreePath(string repositoryRoot, Guid projectId, Guid releaseId)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        if (string.IsNullOrWhiteSpace(local)) return (null, "Could not resolve workstation data directory.");
        var root = Path.GetFullPath(Path.Combine(local, "ProjectBeacon", "release-worktrees"));
        var path = Path.GetFullPath(Path.Combine(root, projectId.ToString("N"), releaseId.ToString("N")));
        if (Within(repositoryRoot, root) || !Within(root, path)) return (null, "Release worktree root is unsafe.");
        return (path, null);
    }

    private static async Task WriteMetadataAsync(string worktree, Metadata metadata, CancellationToken ct)
    {
        var path = worktree + MetadataSuffix;
        if (Reparse(path)) throw new IOException("Release worktree metadata is a symbolic link.");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(metadata, JsonOptions), new UTF8Encoding(false), ct);
    }

    private static Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> Git(
        string directory, CancellationToken ct, IReadOnlyCollection<string> filters, params string[] args) =>
        AgentSessionWorktree.RunGitAsync(directory, ct, filters, args);

    private static async Task<(string? Value, string? Error)> GitValue(
        string directory, CancellationToken ct, IReadOnlyCollection<string> filters, params string[] args)
    {
        var result = await Git(directory, ct, filters, args);
        return Succeeded(result) ? (result.StdOut.Trim(), null) : (null, Clip(result.StdErr));
    }

    private static bool Succeeded((int ExitCode, string StdOut, string StdErr, bool TimedOut) result) =>
        result.ExitCode == 0 && !result.TimedOut;

    private static bool Within(string root, string path)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.Equals(r, comparison) || p.StartsWith(r + Path.DirectorySeparatorChar, comparison);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool Reparse(string path)
    {
        try
        {
            // LinkTarget also detects dangling symlinks, for which File.Exists and
            // Directory.Exists both return false. This matters before opening a path
            // that came from a checked-out remote branch.
            if (new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null)
                return true;
            return (File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; }
    }

    private static bool IsJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1_000_000) return false;
        try { return JsonNode.Parse(json) is JsonObject; } catch (JsonException) { return false; }
    }

    private static string Text(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? string.Empty;
    private static Guid GuidValue(JsonObject obj, string key) => Guid.TryParse(Text(obj, key), out var id) ? id : Guid.Empty;
    private static int PositiveInt(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<int>(out var n) && n > 0 ? n : 0;
    private static bool SameGuid(string raw, Guid expected) => Guid.TryParse(raw, out var actual) && actual == expected;
    private static string Clip(string? value) => string.IsNullOrWhiteSpace(value) ? "No diagnostic details returned." : value.Length > 1200 ? value[..1200] : value;
    private static (bool Ok, string? Result, string? Error) GitFailure(string message, string? error) => Fail(message + " " + Clip(error));
    private static (bool Ok, string? Result, string? Error) Fail(string error) => (false, null, error);
    private static (bool Ok, string? Result, string? Error) Success(object value) => (true, JsonSerializer.Serialize(value, JsonOptions), null);
}
