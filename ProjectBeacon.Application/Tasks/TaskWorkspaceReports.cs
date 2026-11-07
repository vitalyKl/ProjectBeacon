namespace ProjectBeacon.Application.Tasks;

using Domain.Entities.Tasks;
using Domain.Enums;

internal static class TaskWorkspaceReports
{
    public static TaskWorkspaceSection Create(
        Guid taskId, Guid projectId, Guid subtaskId, string content,
        string? abstractText, Guid? sessionId)
    {
        var key = "report/" + subtaskId.ToString("N");
        var title = "Subtask report " + subtaskId.ToString("N")[..8];
        var source = string.IsNullOrWhiteSpace(abstractText) ? content : abstractText.Trim();
        var summary = source[..Math.Min(source.Length, 200)];
        return TaskWorkspaceSection.Create(
            taskId, projectId, key, title, summary, "Report", content,
            "Server", null, PipelineRole.Actor.ToString(), null, subtaskId, sessionId);
    }
}
