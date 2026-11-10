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
