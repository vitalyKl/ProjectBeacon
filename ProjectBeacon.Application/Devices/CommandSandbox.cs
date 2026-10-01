namespace ProjectBeacon.Application.Devices;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Common;
using Domain.Enums;

public static class CommandSandbox
{
    public const string RuntimeRequired = "Project runtime is required.";

    public static bool IsProjectKind(WorkstationCommandKind kind) => kind is
        WorkstationCommandKind.InitProject
        or WorkstationCommandKind.ApplyOpencode
        or WorkstationCommandKind.RunEvalTurn
        or WorkstationCommandKind.RunReviewCheck
        or WorkstationCommandKind.ChatEnsureSession
        or WorkstationCommandKind.ChatPrompt;

    public static Result<string> SanitizeProjectPayload(string? payloadJson)
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

        obj.Remove("root");
        if (obj["path"] is JsonValue value && value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
        {
            if (path.Contains('\0', StringComparison.Ordinal) || Path.IsPathRooted(path))
                return Result.Failure<string>(WorkspacePath.RelativePathRequired);
        }

        return Result.Ok(obj.ToJsonString());
    }
}
