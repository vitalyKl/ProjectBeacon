namespace ProjectBeacon.Web.Features.Chat;

using ProjectBeacon.Domain.Enums;

public static class ChatPhase
{
    public static string Resolve(ChatSessionStatus? status, string? lastKind, bool failed, int partCount)
    {
        if (failed)
            return "Failed";
        if (status == ChatSessionStatus.Aborted)
            return "Aborted";
        if (status == ChatSessionStatus.Streaming)
        {
            if (partCount == 0 || string.IsNullOrWhiteSpace(lastKind) || string.Equals(lastKind, "user", StringComparison.OrdinalIgnoreCase))
                return "Waiting";
            if (string.Equals(lastKind, "tool", StringComparison.OrdinalIgnoreCase))
                return "UsingTool";
            return "Generating";
        }

        if (status == ChatSessionStatus.Idle && partCount > 0)
            return "Completed";
        return "";
    }

    public static string LabelKey(string phase) => phase switch
    {
        "Failed" => "Failed",
        "Aborted" => "ChatAborted",
        "UsingTool" => "ChatUsingTool",
        "Waiting" => "ChatWaitingRuntime",
        "Generating" => "ChatGenerating",
        "Completed" => "ChatCompleted",
        _ => "Chat"
    };
}
