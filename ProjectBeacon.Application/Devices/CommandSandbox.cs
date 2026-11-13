namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Common;
using Domain.Enums;
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
                || obj["sourceSha"] is not JsonValue sourceValue
                || !sourceValue.TryGetValue<string>(out var sourceSha)
                || !IsGitSha(sourceSha)
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
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "projectId", "taskId", "landingAttemptId", "action", "sourceSha",
                "checkCommandHash", "targetHeadSha", "targetTreeSha", "mergeCommitSha"
            };
            if (obj.Any(pair => !allowed.Contains(pair.Key)))
                return Result.Failure<string>("GitLand payload contains unsupported properties.");
            if (action == "Execute" && (obj["targetHeadSha"] is null || obj["targetTreeSha"] is null))
                return Result.Failure<string>("GitLand Execute requires the preview target head and candidate tree.");
            if (action == "Revert" && obj["mergeCommitSha"] is null)
                return Result.Failure<string>("GitLand Revert requires the recorded merge commit.");
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
