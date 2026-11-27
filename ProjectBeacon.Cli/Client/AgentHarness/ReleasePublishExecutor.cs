namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.Entities.Projects;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Mcp;

/// <summary>
/// Publishes an already verified Release Candidate. This is deliberately separate from
/// ReleasePrepare: it may create one immutable annotated tag, then only observes the
/// exact GitHub release workflow and verifies the project's configured production endpoint.
/// It never force-updates or deletes a tag, merges a PR, or deploys directly.
/// </summary>
internal static class ReleasePublishExecutor
{
    private const int MaxPayloadCharacters = 32_000;
    private static readonly TimeSpan WorkflowTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan WorkflowPollInterval = TimeSpan.FromSeconds(15);

    private sealed record PublishRequest(
        Guid ProjectId,
        Guid ReleaseId,
        Guid CandidateId,
        string Version,
        string TargetBranch,
        string TagName,
        string CandidateSha,
        string CandidateTreeSha,
        string DeploymentVersionEndpoint,
        int PublicationRevision,
        int ReleaseRevision);

    private sealed record WorkflowRun(long Id, string HeadSha, string Event, string Status,
        string Conclusion, string HeadBranch, string Url);

    public static async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(
        string? localRoot,
        Guid projectId,
        string? payloadJson,
        CancellationToken ct)
    {
        if (projectId == Guid.Empty || string.IsNullOrWhiteSpace(localRoot) || !Directory.Exists(localRoot))
            return Fail("Release publication requires an existing project runtime root and project id.");
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson.Length > MaxPayloadCharacters)
            return Fail("Release publication payload is missing or exceeds the size limit.");

        if (!TryReadRequest(payloadJson, out var request, out var payloadError))
            return Fail(payloadError ?? "Release publication payload is invalid.");
        if (request.ProjectId != projectId)
            return Fail("Release publication project id does not match the authenticated workstation command.");

        try
        {
            var projectRoot = AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(localRoot));
            var rootResult = await GitValue(projectRoot, ct, [], "rev-parse", "--show-toplevel");
            if (rootResult.Error is not null)
                return Fail("The configured project runtime is not inside a Git repository. " + rootResult.Error);
            var repositoryRoot = AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(rootResult.Value!));
            if (!Within(repositoryRoot, projectRoot))
                return Fail("The project runtime root escapes its Git repository.");
            if (GitRepositoryOperationGate.HasActiveSessions(repositoryRoot))
                return Fail("Release publication is refused while an agent session is active in this repository.");

            using var repositoryGate = await GitRepositoryOperationGate.AcquireAsync(repositoryRoot, ct);
            if (GitRepositoryOperationGate.HasActiveSessions(repositoryRoot))
                return Fail("Release publication is refused while an agent session is active in this repository.");

            var filtersResult = await Git(repositoryRoot, ct, [], "config", "--name-only", "--get-regexp", "^filter\\.");
            if (filtersResult.TimedOut || filtersResult.ExitCode is not (0 or 1))
                return Fail("Could not inspect Git filter configuration safely.");
            var filters = AgentSessionWorktree.ParseFilterDriverNames(filtersResult.StdOut);

            var origin = await ReadOriginAsync(repositoryRoot, filters, ct);
            if (origin.Error is not null)
                return Fail(origin.Error);

            var candidate = await VerifyCandidateCommitAsync(
                repositoryRoot, filters, request.CandidateSha, request.CandidateTreeSha, ct);
            if (candidate.Error is not null)
                return Fail(candidate.Error);

            var tagMessage = BuildTagMessage(request);
            var tag = await EnsureImmutableTagAsync(
                repositoryRoot, filters, origin.Url!, request, tagMessage, ct);
            if (!tag.Success)
            {
                var failure = ResultJson(request, tag.TagPushed, null, null, null, null, null, tag.Error);
                return (false, failure, tag.Error);
            }

            var ghPath = WorkstationProbe.Which(
                "gh", NativeCommandRunner.BuildSafePath(Environment.GetEnvironmentVariable("PATH"), repositoryRoot));
            if (ghPath is null)
            {
                const string message = "The release tag exists, but GitHub CLI is not installed; workflow observation cannot continue.";
                return (false, ResultJson(request, tag.TagPushed, null, null, null, null, null, message), message);
            }

            var auth = await AgentTaskGitOperations.RunGitHubCliAsync(
                ghPath, repositoryRoot, ct, "auth", "status", "--hostname", "github.com");
            if (auth.ExitCode != 0 || auth.TimedOut)
            {
                const string message = "The release tag exists, but GitHub CLI is not authenticated for workflow observation.";
                return (false, ResultJson(request, tag.TagPushed, null, null, null, null, null, message), message);
            }

            var workflow = await ObserveReleaseWorkflowAsync(
                ghPath, repositoryRoot, origin.Repository!, request, ct);
            if (!workflow.Success || workflow.Run is null)
            {
                return (false,
                    ResultJson(request, tag.TagPushed, workflow.Run?.Id, workflow.Run?.Url,
                        workflow.Run?.Status, workflow.Run?.Conclusion, null, workflow.Error),
                    workflow.Error);
            }

            var deployment = await McpApiTools.CheckDeploymentEndpointAsync(
                request.DeploymentVersionEndpoint, request.Version, ct);
            var deploymentStatus = deployment["status"]?.GetValue<string>();
            var deployedSha = deployment["deployedCommitSha"]?.GetValue<string>();
            var deployedVersion = deployment["deployedVersion"]?.GetValue<string>();
            if (!string.Equals(deploymentStatus, "in_sync", StringComparison.Ordinal)
                || !string.Equals(deployedVersion, request.Version, StringComparison.Ordinal)
                || !string.Equals(deployedSha, request.CandidateSha, StringComparison.OrdinalIgnoreCase))
            {
                var message = deploymentStatus switch
                {
                    "unreachable" => "The release workflow succeeded, but the configured production version endpoint is unreachable.",
                    "not_configured" => "The release workflow succeeded, but no production version endpoint is configured.",
                    _ => "The release workflow succeeded, but production version/gitSha does not exactly match the immutable Candidate."
                };
                return (false,
                    ResultJson(request, tag.TagPushed, workflow.Run.Id, workflow.Run.Url,
                        workflow.Run.Status, workflow.Run.Conclusion, deployment, message),
                    message);
            }

            var deployedAtText = deployment["deployedAtUtc"]?.GetValue<string>();
            var deployedAt = DateTime.UtcNow;
            if (DateTime.TryParse(deployedAtText, out var endpointTime))
                deployedAt = DateTime.SpecifyKind(endpointTime.ToUniversalTime(), DateTimeKind.Utc);

            return (true,
                ResultJson(request, true, workflow.Run.Id, workflow.Run.Url,
                    workflow.Run.Status, workflow.Run.Conclusion, deployment, null, deployedAt),
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
        {
            return Fail("Release publication failed safely (" + exception.GetType().Name + "): " + Clip(exception.Message));
        }
    }

    private static bool TryReadRequest(string json, out PublishRequest request, out string? error)
    {
        request = null!;
        error = null;
        try
        {
            var root = JsonNode.Parse(json) as JsonObject
                ?? throw new JsonException("Payload must be an object.");
            if (root.Count != 11)
            {
                error = "PublishRelease payload must contain exactly 11 properties.";
                return false;
            }

            var projectId = GuidValue(root, "projectId");
            var releaseId = GuidValue(root, "releaseId");
            var candidateId = GuidValue(root, "candidateId");
            var version = Text(root, "version");
            var branch = Text(root, "targetBranch");
            var tag = Text(root, "tagName");
            var candidateSha = Text(root, "candidateSha");
            var treeSha = Text(root, "candidateTreeSha");
            var endpoint = Text(root, "deploymentVersionEndpoint");
            var publicationRevision = Integer(root, "publicationRevision");
            var releaseRevision = Integer(root, "releaseRevision");

            if (projectId == Guid.Empty || releaseId == Guid.Empty || candidateId == Guid.Empty
                || string.IsNullOrWhiteSpace(version) || version.Length > 100 || version.Any(char.IsWhiteSpace)
                || !ProjectVersionPolicyCatalog.IsSafeBranchName(branch)
                || !ProjectVersionPolicyCatalog.IsSafeGitTagName(tag)
                || !IsGitSha(candidateSha) || !IsGitSha(treeSha)
                || publicationRevision < 1 || releaseRevision < 1
                || !ProjectVersionPolicyCatalog.TryValidateDeploymentVersionEndpoint(endpoint, out error)
                || string.IsNullOrWhiteSpace(endpoint))
            {
                error ??= "PublishRelease payload contains invalid identity, version, branch, tag, SHA or endpoint values.";
                return false;
            }

            request = new PublishRequest(projectId, releaseId, candidateId, version, branch, tag,
                candidateSha.ToLowerInvariant(), treeSha.ToLowerInvariant(), endpoint,
                publicationRevision, releaseRevision);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            error = "PublishRelease payload is invalid: " + Clip(exception.Message);
            return false;
        }
    }

    private static async Task<(string? Url, string? Repository, string? Error)> ReadOriginAsync(
        string root, string[] filters, CancellationToken ct)
    {
        var fetch = await Git(root, ct, filters, "remote", "get-url", "--all", "origin");
        if (!Succeeded(fetch))
            return (null, null, "Release publication requires a configured GitHub origin.");
        var urls = fetch.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (urls.Length != 1 || !AgentTaskGitOperations.TryParseGitHubRemote(urls[0].Trim(), out var repository))
            return (null, null, "Release publication requires exactly one GitHub HTTPS or SSH origin.");
        var push = await Git(root, ct, filters, "remote", "get-url", "--push", "--all", "origin");
        var pushUrls = push.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!Succeeded(push) || pushUrls.Length != 1
            || !AgentTaskGitOperations.TryParseGitHubRemote(pushUrls[0].Trim(), out var pushRepository)
            || !string.Equals(repository, pushRepository, StringComparison.OrdinalIgnoreCase))
            return (null, null, "Origin fetch and push URLs must target the same GitHub repository.");
        if (!AgentTaskWorktree.IsSupportedFetchRemote(urls[0].Trim()))
            return (null, null, "Configured Git origin uses an unsupported transport.");
        return (urls[0].Trim(), repository, null);
    }

    private static async Task<(bool Success, string? Error)> VerifyCandidateCommitAsync(
        string root, string[] filters, string candidateSha, string candidateTreeSha, CancellationToken ct)
    {
        var commit = await GitValue(root, ct, filters, "cat-file", "-t", candidateSha);
        if (!string.Equals(commit.Value, "commit", StringComparison.Ordinal))
            return (false, "The immutable Candidate SHA is not an available Git commit in the repository.");
        var tree = await GitValue(root, ct, filters, "rev-parse", candidateSha + "^{tree}");
        if (tree.Error is not null || !string.Equals(tree.Value, candidateTreeSha, StringComparison.OrdinalIgnoreCase))
            return (false, "The Candidate tree SHA no longer matches the immutable Release snapshot.");
        return (true, null);
    }

    private static string BuildTagMessage(PublishRequest request) =>
        "ProjectBeacon Release " + request.Version + Environment.NewLine
        + "ProjectBeacon-Release: " + request.ReleaseId.ToString("D") + Environment.NewLine
        + "ProjectBeacon-Candidate: " + request.CandidateId.ToString("D") + Environment.NewLine
        + "ProjectBeacon-Candidate-SHA: " + request.CandidateSha + Environment.NewLine
        + "ProjectBeacon-Version: " + request.Version;

    private sealed record TagEvidence(bool Success, bool TagPushed, string? Error);

    /// <summary>
    /// Only a successful, well-formed remote inspection that proves the tag is absent
    /// or points elsewhere can establish that the Candidate was not published.
    /// Any failed or malformed inspection is conservatively treated as possibly pushed.
    /// </summary>
    internal static bool MayHavePublishedTag(
        bool remoteInspectionSucceeded,
        bool remoteListingWellFormed,
        bool tagReferencePresent,
        bool peeledReferencePresent,
        string? peeledTargetSha,
        string candidateSha)
    {
        if (!remoteInspectionSucceeded || !remoteListingWellFormed)
            return true;
        if (!tagReferencePresent && !peeledReferencePresent)
            return false;
        if (!tagReferencePresent || !peeledReferencePresent)
            return true;
        return string.Equals(peeledTargetSha, candidateSha, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<TagEvidence> EnsureImmutableTagAsync(
        string root, string[] filters, string originUrl, PublishRequest request,
        string expectedMessage, CancellationToken ct)
    {
        var remote = await AgentTaskGitOperations.RunRemoteGitAsync(
            root, filters, ct, originUrl, "ls-remote", "--tags", originUrl,
            "refs/tags/" + request.TagName, "refs/tags/" + request.TagName + "^{}");
        if (!Succeeded(remote))
            return new TagEvidence(false, true,
                "Could not inspect the remote release tag. The tag may already exist; refusing to classify this publication as pre-tag.");

        var remoteEntries = ParseLsRemote(remote.StdOut);
        if (remoteEntries.Error is not null)
            return new TagEvidence(false, true,
                "Could not safely interpret the remote release tag listing; the tag may already exist. " + remoteEntries.Error);
        var hasRemote = remoteEntries.ShaByRef.ContainsKey("refs/tags/" + request.TagName)
            || remoteEntries.ShaByRef.ContainsKey("refs/tags/" + request.TagName + "^{}");

        var localTag = await ReadTagEvidenceAsync(root, filters, request, ct);
        if (localTag.Exists && !localTag.Valid)
            return new TagEvidence(false, hasRemote,
                "A local tag with this name exists but does not match the immutable Candidate. Refusing to overwrite it.");

        if (hasRemote && !localTag.Exists)
        {
            var fetch = await AgentTaskGitOperations.RunRemoteGitAsync(
                root, filters, ct, originUrl, "fetch", "--no-tags", "--no-recurse-submodules",
                originUrl, "refs/tags/" + request.TagName + ":refs/tags/" + request.TagName);
            if (!Succeeded(fetch))
                return new TagEvidence(false, true,
                    "The remote release tag already exists but could not be fetched and verified. Refusing to overwrite it.");
            localTag = await ReadTagEvidenceAsync(root, filters, request, ct);
            var advertisedTagObjectSha = remoteEntries.ShaByRef.GetValueOrDefault("refs/tags/" + request.TagName);
            if (!localTag.Valid
                || !string.Equals(localTag.TagObjectSha, advertisedTagObjectSha, StringComparison.OrdinalIgnoreCase))
                return new TagEvidence(false, true,
                    localTag.Error ?? "The remote tag object changed while it was being fetched. Refusing to accept unverified metadata.");
        }
        else if (hasRemote && localTag.Exists)
        {
            // The server may have created the tag while the previous acknowledgement was lost.
            // Compare both the annotated object SHA and peeled commit; never force-update either side.
            var tagReferencePresent = remoteEntries.ShaByRef.TryGetValue(
                "refs/tags/" + request.TagName, out var remoteTagObjectSha);
            var peeledReferencePresent = remoteEntries.ShaByRef.TryGetValue(
                "refs/tags/" + request.TagName + "^{}", out var remotePeeledSha);
            var remoteTargetMatches = peeledReferencePresent
                ? string.Equals(remotePeeledSha, request.CandidateSha, StringComparison.OrdinalIgnoreCase)
                : tagReferencePresent
                    && string.Equals(remoteTagObjectSha, localTag.TagObjectSha, StringComparison.OrdinalIgnoreCase);
            if (!remoteTargetMatches
                || !tagReferencePresent
                || !string.Equals(remoteTagObjectSha, localTag.TagObjectSha, StringComparison.OrdinalIgnoreCase))
                return new TagEvidence(false,
                    MayHavePublishedTag(true, true, tagReferencePresent, peeledReferencePresent,
                        remotePeeledSha, request.CandidateSha),
                    "The remote tag object or target differs from the locally verified immutable Candidate. Refusing to move it.");
        }

        if (localTag.Exists && !localTag.Valid)
            return new TagEvidence(false, hasRemote, "The local annotated tag does not contain the expected Release/Candidate identity.");

        // Reaching this point with hasRemote=true means the remote annotated tag
        // has been fetched or matched to the exact locally verified Candidate object.
        var tagPushed = hasRemote;
        if (!localTag.Exists)
        {
            var create = await Git(root, ct, filters, "tag", "-a", request.TagName,
                request.CandidateSha, "-m", expectedMessage);
            if (!Succeeded(create))
            {
                // A concurrent create may have won. Accept only after reading and validating
                // the resulting annotated tag object.
                localTag = await ReadTagEvidenceAsync(root, filters, request, ct);
                if (!localTag.Valid)
                    return new TagEvidence(false, false, "Git could not create the immutable release tag: " + Clip(create.StdErr));
            }
            else
            {
                localTag = await ReadTagEvidenceAsync(root, filters, request, ct);
            }
        }
        if (!localTag.Valid)
            return new TagEvidence(false, tagPushed, "Created release tag failed its identity and target verification.");

        if (!hasRemote)
        {
            var push = await AgentTaskGitOperations.RunRemoteGitAsync(
                root, filters, ct, originUrl, "push", "--no-follow-tags", "--porcelain",
                originUrl, "refs/tags/" + request.TagName);
            if (!Succeeded(push))
            {
                // A rejected or interrupted push is ambiguous. Read the remote again below;
                // never use a force push as recovery.
                var afterFailedPush = await AgentTaskGitOperations.RunRemoteGitAsync(
                    root, filters, ct, originUrl, "ls-remote", "--tags", originUrl,
                    "refs/tags/" + request.TagName, "refs/tags/" + request.TagName + "^{}");
                var after = ParseLsRemote(afterFailedPush.StdOut);
                if (!Succeeded(afterFailedPush) || after.Error is not null)
                    return new TagEvidence(false, true,
                        "The tag push outcome is ambiguous and the remote tag could not be conclusively inspected. Treating the tag as potentially published; retry only with the same immutable Candidate. "
                        + (after.Error ?? Clip(afterFailedPush.StdErr)));
                var tagReferencePresent = after.ShaByRef.ContainsKey("refs/tags/" + request.TagName);
                var peeledReferencePresent = after.ShaByRef.TryGetValue(
                    "refs/tags/" + request.TagName + "^{}", out var afterPeeledSha);
                if (!MayHavePublishedTag(true, true, tagReferencePresent,
                    peeledReferencePresent, afterPeeledSha, request.CandidateSha))
                    return new TagEvidence(false, false,
                        "The push failed and a successful remote inspection confirms that the Candidate tag was not published. "
                        + Clip(push.StdErr));

                // A competing writer may have created the same tag name while our push
                // acknowledgement was lost. Matching the peeled commit is insufficient:
                // fetch the remote tag object to a temporary ref and verify its annotation.
                var remoteEvidence = await ReadRemoteTagEvidenceAsync(root, filters, originUrl, request, ct);
                var advertisedObjectSha = after.ShaByRef.GetValueOrDefault("refs/tags/" + request.TagName);
                if (!remoteEvidence.Valid
                    || !string.Equals(remoteEvidence.TagObjectSha, advertisedObjectSha, StringComparison.OrdinalIgnoreCase))
                    return new TagEvidence(false, true,
                        remoteEvidence.Error ?? "The remote tag object changed during concurrent publication; its metadata could not be proven.");
                tagPushed = true;
            }
            else
            {
                tagPushed = true;
            }
        }

        var remoteVerify = await AgentTaskGitOperations.RunRemoteGitAsync(
            root, filters, ct, originUrl, "ls-remote", "--tags", originUrl,
            "refs/tags/" + request.TagName, "refs/tags/" + request.TagName + "^{}");
        if (!Succeeded(remoteVerify))
            return new TagEvidence(false, tagPushed, "Could not verify the exact release tag on the remote.");
        var verify = ParseLsRemote(remoteVerify.StdOut);
        if (verify.Error is not null)
            return new TagEvidence(false, tagPushed, "Could not safely interpret the final remote tag evidence: " + verify.Error);
        var finalTagReferencePresent = verify.ShaByRef.ContainsKey("refs/tags/" + request.TagName);
        var finalPeeledReferencePresent = verify.ShaByRef.TryGetValue(
            "refs/tags/" + request.TagName + "^{}", out var finalPeeledSha);
        if (!MayHavePublishedTag(true, true, finalTagReferencePresent,
            finalPeeledReferencePresent, finalPeeledSha, request.CandidateSha))
            return new TagEvidence(false, tagPushed,
                "The remote release tag is absent or resolves to a different commit than the immutable Candidate.");
        var finalEvidence = await ReadRemoteTagEvidenceAsync(root, filters, originUrl, request, ct);
        var finalObjectSha = verify.ShaByRef.GetValueOrDefault("refs/tags/" + request.TagName);
        if (!finalTagReferencePresent
            || !finalEvidence.Valid
            || !string.Equals(finalEvidence.TagObjectSha, finalObjectSha, StringComparison.OrdinalIgnoreCase))
            return new TagEvidence(false, tagPushed,
                finalEvidence.Error ?? "The remote annotated tag object changed during final verification.");
        return new TagEvidence(true, true, null);
    }

    private sealed record TagRead(bool Exists, bool Valid, string? TagObjectSha, string? Error);

    private static async Task<TagRead> ReadTagEvidenceAsync(
        string root, string[] filters, PublishRequest request, CancellationToken ct, string? referenceOverride = null)
    {
        var reference = referenceOverride ?? "refs/tags/" + request.TagName;
        var type = await GitValue(root, ct, filters, "cat-file", "-t", reference);
        if (!string.Equals(type.Value, "tag", StringComparison.Ordinal))
            return new TagRead(type.Value is not null, false, null, "Release tag must be annotated.");
        var tagObject = await GitValue(root, ct, filters, "cat-file", "-p", reference);
        var target = await GitValue(root, ct, filters, "rev-parse", reference + "^{}");
        var objectSha = await GitValue(root, ct, filters, "rev-parse", reference);
        if (tagObject.Error is not null || target.Error is not null || objectSha.Error is not null
            || !AgentTaskGitOperations.IsGitSha(objectSha.Value ?? string.Empty))
            return new TagRead(true, false, objectSha.Value, "Could not inspect annotated release tag metadata.");
        var valid = HasExpectedAnnotatedTagEvidence(
            type.Value, tagObject.Value!, target.Value!, request.TagName,
            request.ReleaseId, request.CandidateId, request.CandidateSha, request.Version);
        return new TagRead(true, valid, objectSha.Value,
            valid ? null : "Annotated tag object does not prove the exact Release, Candidate identity, version, tag name and target SHA.");
    }

    private static async Task<TagRead> ReadRemoteTagEvidenceAsync(
        string root, string[] filters, string originUrl, PublishRequest request, CancellationToken ct)
    {
        var temporaryReference = "refs/beacon/release-tag-verification/"
            + request.ReleaseId.ToString("N") + "/" + Guid.NewGuid().ToString("N");
        try
        {
            // Include fetch inside the try: cancellation can arrive after Git writes the
            // local verification ref but before the process reports completion.
            var fetched = await AgentTaskGitOperations.RunRemoteGitAsync(
                root, filters, ct, originUrl,
                "fetch", "--no-tags", "--no-recurse-submodules", originUrl,
                "refs/tags/" + request.TagName + ":" + temporaryReference);
            if (!Succeeded(fetched))
                return new TagRead(false, false, null, "Could not fetch the remote annotated release tag for identity verification: " + Clip(fetched.StdErr));
            return await ReadTagEvidenceAsync(root, filters, request, ct, temporaryReference);
        }
        finally
        {
            // The ref is local-only and unique. Cleanup must still run after request
            // cancellation; a failed cleanup cannot mutate the remote tag.
            try
            {
                _ = await Git(root, CancellationToken.None, filters, "update-ref", "-d", temporaryReference);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // A leaked, random verification ref is harmless and never reused.
            }
        }
    }

    internal static bool HasExpectedAnnotatedTagEvidence(
        string objectType,
        string tagObject,
        string targetSha,
        string tagName,
        Guid releaseId,
        Guid candidateId,
        string candidateSha,
        string version)
    {
        if (!string.Equals(objectType, "tag", StringComparison.Ordinal)
            || !AgentTaskGitOperations.IsGitSha(targetSha)
            || !AgentTaskGitOperations.IsGitSha(candidateSha)
            || !string.Equals(targetSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            return false;

        var separator = tagObject.IndexOf("\n\n", StringComparison.Ordinal);
        if (separator < 0)
            return false;
        // Git tag objects use LF delimiters. Split only on LF so a stray CR remains
        // part of the line and cannot be normalized away before exact identity comparison.
        var header = tagObject[..separator].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var messageLines = tagObject[(separator + 2)..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        static bool HasSingleExactLine(IEnumerable<string> lines, string expected) =>
            lines.Count(line => string.Equals(line, expected, StringComparison.Ordinal)) == 1;

        return HasSingleExactLine(header, "object " + candidateSha)
            && HasSingleExactLine(header, "type commit")
            && HasSingleExactLine(header, "tag " + tagName)
            && HasSingleExactLine(messageLines, "ProjectBeacon Release " + version)
            && HasSingleExactLine(messageLines, "ProjectBeacon-Release: " + releaseId.ToString("D"))
            && HasSingleExactLine(messageLines, "ProjectBeacon-Candidate: " + candidateId.ToString("D"))
            && HasSingleExactLine(messageLines, "ProjectBeacon-Candidate-SHA: " + candidateSha)
            && HasSingleExactLine(messageLines, "ProjectBeacon-Version: " + version);
    }

    private sealed record RemoteTagMap(Dictionary<string, string> ShaByRef, string? Error);

    private static RemoteTagMap ParseLsRemote(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('\t', 2);
            if (parts.Length != 2 || !IsGitSha(parts[0]))
                return new RemoteTagMap(result, "Remote returned malformed release tag metadata.");
            result[parts[1]] = parts[0].ToLowerInvariant();
        }
        return new RemoteTagMap(result, null);
    }

    private sealed record WorkflowPoll(bool Success, WorkflowRun? Run, string? Error);

    private static async Task<WorkflowPoll> ObserveReleaseWorkflowAsync(
        string ghPath, string workingDirectory, string repository, PublishRequest request, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        WorkflowRun? lastRun = null;
        while (DateTime.UtcNow - start < WorkflowTimeout)
        {
            ct.ThrowIfCancellationRequested();
            var list = await AgentTaskGitOperations.RunGitHubCliAsync(
                ghPath, workingDirectory, ct,
                "run", "list", "--repo", repository,
                "--workflow", "release.yml", "--limit", "30",
                "--json", "databaseId,headSha,event,status,conclusion,headBranch,url");
            if (list.ExitCode != 0 || list.TimedOut)
                return new WorkflowPoll(false, lastRun, "Could not query GitHub release workflow status: " + Clip(list.StdErr));

            var parsed = ParseWorkflowRuns(list.StdOut);
            if (parsed.Error is not null)
                return new WorkflowPoll(false, lastRun, parsed.Error);
            lastRun = parsed.Runs
                .Where(run => string.Equals(run.HeadSha, request.CandidateSha, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(run.Event, "push", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(run.HeadBranch)
                        || string.Equals(run.HeadBranch, request.TagName, StringComparison.Ordinal)))
                .OrderByDescending(run => run.Id)
                .FirstOrDefault();
            if (lastRun is not null && string.Equals(lastRun.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(lastRun.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
                    return new WorkflowPoll(true, lastRun, null);

                return new WorkflowPoll(false, lastRun,
                    "GitHub release workflow completed with conclusion '" + lastRun.Conclusion + "'. " +
                    "The immutable tag is already published; correct the workflow failure and retry publication to observe its rerun.");
            }
            await Task.Delay(WorkflowPollInterval, ct);
        }
        return new WorkflowPoll(false, lastRun,
            "Timed out waiting for the release workflow for the exact Candidate SHA. The tag is not rewritten; publication can be retried after inspecting GitHub Actions.");
    }

    private static (IReadOnlyList<WorkflowRun> Runs, string? Error) ParseWorkflowRuns(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return ([], "GitHub CLI returned an invalid workflow-run list.");
            var values = new List<WorkflowRun>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = Integer(item, "databaseId");
                var headSha = Text(item, "headSha");
                var eventName = Text(item, "event");
                var status = Text(item, "status");
                var conclusion = Text(item, "conclusion");
                var branch = Text(item, "headBranch");
                var url = Text(item, "url");
                if (id <= 0 || !IsGitSha(headSha) || string.IsNullOrWhiteSpace(eventName)
                    || string.IsNullOrWhiteSpace(status) || !IsWorkflowRunUrl(url))
                    return ([], "GitHub CLI returned incomplete workflow-run evidence.");
                values.Add(new WorkflowRun(id, headSha, eventName, status, conclusion, branch, url));
            }
            return (values, null);
        }
        catch (JsonException)
        {
            return ([], "GitHub CLI returned malformed workflow-run JSON.");
        }
    }

    private static string ResultJson(
        PublishRequest request, bool tagPushed, long? runId, string? runUrl, string? workflowStatus,
        string? conclusion, JsonObject? deployment, string? error, DateTime? deployedAt = null) => new JsonObject
        {
            ["action"] = "Publish",
            ["projectId"] = request.ProjectId.ToString("D"),
            ["releaseId"] = request.ReleaseId.ToString("D"),
            ["candidateId"] = request.CandidateId.ToString("D"),
            ["version"] = request.Version,
            ["tagName"] = request.TagName,
            ["candidateSha"] = request.CandidateSha,
            ["tagPushed"] = tagPushed,
            ["workflowRunId"] = runId,
            ["workflowRunUrl"] = runUrl,
            ["workflowStatus"] = workflowStatus,
            ["workflowConclusion"] = conclusion,
            ["deployedVersion"] = deployment?["deployedVersion"]?.DeepClone(),
            ["deployedCommitSha"] = deployment?["deployedCommitSha"]?.DeepClone(),
            ["deploymentStatus"] = deployment?["status"]?.DeepClone(),
            ["deployedAtUtc"] = deployedAt?.ToString("O"),
            ["error"] = error
        }.ToJsonString();

    private static bool IsWorkflowRunUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(uri.AbsolutePath, @"^/[^/]+/[^/]+/actions/runs/[1-9][0-9]*$", RegexOptions.CultureInvariant);

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
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullRoot, fullPath, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsGitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static string Text(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? string.Empty;
    private static string Text(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    private static Guid GuidValue(JsonObject obj, string key) => Guid.TryParse(Text(obj, key), out var id) ? id : Guid.Empty;
    private static int Integer(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    private static long Integer(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private static string Clip(string? value) => string.IsNullOrWhiteSpace(value) ? "No diagnostic details returned." : value.Length > 1200 ? value[..1200] : value;
    private static (bool Ok, string? Result, string? Error) Fail(string error) => (false, null, error);
}
