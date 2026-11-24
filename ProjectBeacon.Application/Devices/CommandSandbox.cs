namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Common;
using Domain.Enums;
using Application.Projects;
using Domain.Entities.Projects;
/// <summary>
/// Strips a client-supplied root from a command payload and rejects paths that are not relative.
/// </summary>
public static class CommandSandbox
{
    public const string RuntimeRequired = "Project runtime is required.";

    private static bool TryGuid(JsonObject value, string name) =>
        value[name] is JsonValue element && element.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && id != Guid.Empty;

    public static bool IsProjectKind(WorkstationCommandKind kind) =>
        WorkstationCommandCatalog.Get(kind).Scope == WorkstationCommandScope.ProjectRuntime;

    private static bool IsGitSha(string value) => (value.Length is 40 or 64) && value.All(Uri.IsHexDigit);
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public static Result<string> SanitizeProjectPayload(string? payloadJson, WorkstationCommandKind? kind = null)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return Result.Ok("{}");

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return Result.Failure<string>("payload must be valid JSON.");
        }

        if (node is not JsonObject obj)
            return Result.Failure<string>("payload must be a JSON object.");

        if (kind == WorkstationCommandKind.ApplyProposalPatch)
        {
            if (obj.Count != 5
                || !TryGuid(obj, "projectId")
                || !TryGuid(obj, "proposalId")
                || obj["patch"] is not JsonValue patchValue
                || !patchValue.TryGetValue<string>(out var patch)
                || string.IsNullOrWhiteSpace(patch)
                || patch.Length > 30_000
                || obj["files"] is not JsonArray files
                || files.Count is < 1 or > 50
                || obj["allowedPaths"] is not JsonArray allowedPaths
                || allowedPaths.Count is < 1 or > 10)
                return Result.Failure<string>("ApplyProposalPatch payload is invalid.");
            return Result.Ok(obj.ToJsonString());
        }

        if (kind == WorkstationCommandKind.GitLand)
        {
            if (!TryGuid(obj, "projectId")
                || !TryGuid(obj, "taskId")
                || !TryGuid(obj, "landingAttemptId")
                || obj["action"] is not JsonValue actionValue
                || !actionValue.TryGetValue<string>(out var action)
                || action is not ("Preview" or "Execute" or "Revert")
                || obj["targetBranch"] is not JsonValue branchValue
                || !branchValue.TryGetValue<string>(out var targetBranch)
                || string.IsNullOrWhiteSpace(targetBranch)
                || targetBranch.Length > 200
                || targetBranch.Contains("..", StringComparison.Ordinal)
                || targetBranch.Contains("@{", StringComparison.Ordinal)
                || targetBranch.Contains('\\')
                || targetBranch.Any(char.IsControl)
                || obj["sourceSha"] is not JsonValue sourceValue
                || !sourceValue.TryGetValue<string>(out var sourceSha)
                || !IsGitSha(sourceSha)
                || obj["reviewTreeSha"] is not JsonValue reviewTreeValue
                || !reviewTreeValue.TryGetValue<string>(out var reviewTreeSha)
                || !IsGitSha(reviewTreeSha)
                || obj["checkCommandHash"] is not JsonValue hashValue
                || !hashValue.TryGetValue<string>(out var checkHash)
                || !IsSha256(checkHash))
                return Result.Failure<string>("GitLand payload is invalid.");
            foreach (var key in new[] { "targetHeadSha", "targetTreeSha", "mergeCommitSha" })
            {
                if (obj[key] is null)
                    continue;
                if (obj[key] is not JsonValue shaValue
                    || !shaValue.TryGetValue<string>(out var sha)
                    || !IsGitSha(sha))
                    return Result.Failure<string>($"GitLand {key} is invalid.");
            }
            if (obj["maxChangedFiles"] is not JsonValue maxFilesValue
                || !maxFilesValue.TryGetValue<int>(out var maxFiles)
                || maxFiles is < 1 or > 200
                || obj["maxChangedLines"] is not JsonValue maxLinesValue
                || !maxLinesValue.TryGetValue<int>(out var maxLines)
                || maxLines is < 1 or > 20_000
                || obj["protectedPaths"] is not JsonArray protectedPaths
                || protectedPaths.Count > 100
                || protectedPaths.Any(item =>
                    item is not JsonValue value
                    || !value.TryGetValue<string>(out var pattern)
                    || !ProjectGitPolicyCatalog.IsSafePattern(pattern)))
                return Result.Failure<string>("GitLand policy constraints are invalid.");

            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "projectId", "taskId", "landingAttemptId", "action", "targetBranch", "sourceSha",
                "reviewTreeSha", "checkCommandHash", "targetHeadSha", "targetTreeSha", "mergeCommitSha",
                "maxChangedFiles", "maxChangedLines", "protectedPaths"
            };
            if (obj.Any(pair => !allowed.Contains(pair.Key)))
                return Result.Failure<string>("GitLand payload contains unsupported properties.");
            if (action == "Execute" && (obj["targetHeadSha"] is null || obj["targetTreeSha"] is null))
                return Result.Failure<string>("GitLand Execute requires the preview target head and candidate tree.");
            if (action == "Revert" && obj["mergeCommitSha"] is null)
                return Result.Failure<string>("GitLand Revert requires the recorded merge commit.");
            return Result.Ok(obj.ToJsonString());
        }

        if (kind == WorkstationCommandKind.ReleasePrepare)
        {
            var action = obj["action"] is JsonValue actionValue
                && actionValue.TryGetValue<string>(out var actionText) ? actionText : null;
            var projectId = obj["projectId"] is JsonValue projectValue
                && projectValue.TryGetValue<string>(out var projectText)
                && Guid.TryParse(projectText, out var parsedProjectId) ? parsedProjectId : Guid.Empty;
            var releaseId = obj["releaseId"] is JsonValue releaseValue
                && releaseValue.TryGetValue<string>(out var releaseText)
                && Guid.TryParse(releaseText, out var parsedReleaseId) ? parsedReleaseId : Guid.Empty;
            var candidateId = obj["candidateId"] is JsonValue candidateValue
                && candidateValue.TryGetValue<string>(out var candidateText)
                && Guid.TryParse(candidateText, out var parsedCandidateId) ? parsedCandidateId : Guid.Empty;
            var branch = obj["targetBranch"] is JsonValue branchValue
                && branchValue.TryGetValue<string>(out var branchText) ? branchText : null;
            var version = obj["version"] is JsonValue versionValue
                && versionValue.TryGetValue<string>(out var versionText) ? versionText : null;
            var policyJson = obj["versionPolicyJson"] is JsonValue policyValue
                && policyValue.TryGetValue<string>(out var policyText) ? policyText : null;
            var versionPolicyRevision = obj["versionPolicyRevision"] is JsonValue policyRevisionValue
                && policyRevisionValue.TryGetValue<int>(out var parsedPolicyRevision) ? parsedPolicyRevision : 0;
            var releaseRevision = obj["releaseRevision"] is JsonValue releaseRevisionValue
                && releaseRevisionValue.TryGetValue<int>(out var parsedReleaseRevision) ? parsedReleaseRevision : 0;
            var snapshotJson = obj["snapshotJson"] is JsonValue snapshotValue
                && snapshotValue.TryGetValue<string>(out var snapshotText) ? snapshotText : null;

            if (action is not ("Prepare" or "VerifyLanding")
                || projectId == Guid.Empty || releaseId == Guid.Empty || candidateId == Guid.Empty
                || !ProjectVersionPolicyCatalog.IsSafeBranchName(branch)
                || versionPolicyRevision < 1 || releaseRevision < 1
                || string.IsNullOrWhiteSpace(snapshotJson) || snapshotJson.Length > 1_000_000)
                return Result.Failure<string>("ReleasePrepare payload is invalid.");

            if (!ProjectVersionPolicyCatalog.TryRead(policyJson, out var policy, out var policyError))
                return Result.Failure<string>(policyError ?? "ReleasePrepare version policy is invalid.");
            if (!policy.ReleaseManagementEnabled)
                return Result.Failure<string>("Release Management is disabled for this project.");
            if (!ProjectVersionPolicyCatalog.TryNormalizeVersion(policy, version, out _, out var versionError))
                return Result.Failure<string>(versionError ?? "ReleasePrepare version is invalid.");

            try
            {
                using var snapshot = JsonDocument.Parse(snapshotJson!);
                if (snapshot.RootElement.ValueKind != JsonValueKind.Object)
                    return Result.Failure<string>("ReleasePrepare snapshotJson must be a JSON object.");
            }
            catch (JsonException)
            {
                return Result.Failure<string>("ReleasePrepare snapshotJson is invalid JSON.");
            }

            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "action", "projectId", "releaseId", "candidateId", "targetBranch", "version",
                "versionPolicyJson", "versionPolicyRevision", "releaseRevision", "snapshotJson"
            };
            if (action == "VerifyLanding")
            {
                allowed.Add("preparedCommitSha");
                allowed.Add("manifestSha256");
                var prepared = obj["preparedCommitSha"] is JsonValue preparedValue
                    && preparedValue.TryGetValue<string>(out var preparedText) ? preparedText : null;
                var manifestHash = obj["manifestSha256"] is JsonValue hashValue
                    && hashValue.TryGetValue<string>(out var hashText) ? hashText : null;
                if (prepared is not { Length: 40 or 64 } || !prepared.All(Uri.IsHexDigit)
                    || manifestHash is not { Length: 64 } || !manifestHash.All(Uri.IsHexDigit))
                    return Result.Failure<string>("ReleasePrepare VerifyLanding SHA values are invalid.");
            }
            if (obj.Any(pair => !allowed.Contains(pair.Key)))
                return Result.Failure<string>("ReleasePrepare payload contains unsupported properties.");

            return Result.Ok(obj.ToJsonString());
        }

        if (kind == WorkstationCommandKind.GitSync)
        {
            if (obj.Count != 2
                || obj["projectId"] is not JsonValue projectIdValue
                || !projectIdValue.TryGetValue<string>(out var projectIdText)
                || !Guid.TryParse(projectIdText, out _)
                || obj["mode"] is not JsonValue modeValue
                || !modeValue.TryGetValue<string>(out var mode)
                || mode is not ("Status" or "PullFastForward"))
                return Result.Failure<string>("GitSync payload must contain only projectId and mode (Status or PullFastForward).");
            return Result.Ok(obj.ToJsonString());
        }

        obj.Remove("root");
        if (kind == WorkstationCommandKind.RunReviewCheck)
            obj.Remove("checkCommand");
        if (obj["path"] is JsonValue value && value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
        {
            if (path.Contains('\0', StringComparison.Ordinal) || WorkspacePath.IsRootedPortable(path) || WorkspacePath.HasParentSegment(path))
                return Result.Failure<string>(WorkspacePath.RelativePathRequired);
        }

        return Result.Ok(obj.ToJsonString());
    }
}
