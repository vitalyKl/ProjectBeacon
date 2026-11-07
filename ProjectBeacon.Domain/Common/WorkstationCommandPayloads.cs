namespace ProjectBeacon.Domain.Common;

using System.Text.Json;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// Typed readers for persisted workstation command payloads that participate in
/// harness authorization. Keep validation here so token issuance and token use
/// interpret the same security scope.
/// </summary>
public static class WorkstationCommandPayloads
{
    public sealed record PipelineSession(
        Guid ProjectId,
        Guid TaskId,
        Guid SessionId,
        PipelineRole Role,
        Guid? SubtaskId);

    public sealed record EvalTurn(Guid EvalRunId, Guid TaskId, string Runtime);

    public sealed record ChatPrompt(Guid ChatSessionId, string ExternalSessionId);

    public static bool TryReadPipelineSession(string? json, out PipelineSession payload)
    {
        payload = default!;
        if (!TryReadObject(json, out var root))
            return false;

        if (!TryReadGuid(root, "projectId", out var projectId)
            || !TryReadGuid(root, "taskId", out var taskId)
            || !TryReadGuid(root, "sessionId", out var sessionId)
            || !root.TryGetProperty("role", out var roleValue)
            || roleValue.ValueKind != JsonValueKind.String
            || !Enum.TryParse(roleValue.GetString(), ignoreCase: true, out PipelineRole role)
            || !Enum.IsDefined(role)
            || !TryReadNullableGuid(root, "subtaskId", out var subtaskId))
            return false;

        payload = new PipelineSession(projectId, taskId, sessionId, role, subtaskId);
        return true;
    }

    public static bool TryReadEvalTurn(string? json, out EvalTurn payload)
    {
        payload = default!;
        if (!TryReadObject(json, out var root)
            || !TryReadGuid(root, "evalRunId", out var evalRunId)
            || !TryReadGuid(root, "taskId", out var taskId)
            || !root.TryGetProperty("runtime", out var runtimeValue)
            || runtimeValue.ValueKind != JsonValueKind.String)
            return false;

        var runtime = runtimeValue.GetString()?.Trim().ToLowerInvariant();
        if (runtime is not ("native" or "opencode"))
            return false;

        payload = new EvalTurn(evalRunId, taskId, runtime);
        return true;
    }

    public static bool TryReadChatPrompt(string? json, out ChatPrompt payload)
    {
        payload = default!;
        if (!TryReadObject(json, out var root)
            || !TryReadGuid(root, "chatSessionId", out var chatSessionId)
            || !root.TryGetProperty("externalSessionId", out var externalSessionValue)
            || externalSessionValue.ValueKind != JsonValueKind.String)
            return false;

        var externalSessionId = externalSessionValue.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(externalSessionId))
            return false;

        payload = new ChatPrompt(chatSessionId, externalSessionId);
        return true;
    }

    private static bool TryReadObject(string? json, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return false;
            }

            // The returned element is cloned so its backing document can be disposed safely.
            root = document.RootElement.Clone();
            document.Dispose();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out value)
            && value != Guid.Empty;
    }

    private static bool TryReadNullableGuid(JsonElement root, string name, out Guid? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String
            || !Guid.TryParse(element.GetString(), out var parsed)
            || parsed == Guid.Empty)
            return false;
        value = parsed;
        return true;
    }
}
