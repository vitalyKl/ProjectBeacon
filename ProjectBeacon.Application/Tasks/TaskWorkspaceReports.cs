namespace ProjectBeacon.Application.Tasks;

using Domain.Entities.Tasks;
using Domain.Enums;

internal static class TaskWorkspaceReports
{
    public static TaskWorkspaceSection Create(
        Guid taskId, Guid projectId, Guid subtaskId, int attempt, string content,
        string? abstractText, Guid? sessionId, string? profileKey = null)
    {
        var key = "report/" + subtaskId.ToString("N") + "/attempt-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var title = "Subtask report " + subtaskId.ToString("N")[..8];
        var source = string.IsNullOrWhiteSpace(abstractText) ? content : abstractText.Trim();
        var summary = source[..Math.Min(source.Length, 200)];
        return TaskWorkspaceSection.Create(
            taskId, projectId, key, title, summary, "Report", content,
            "Server", null, PipelineRole.Actor.ToString(), profileKey, subtaskId, sessionId);
    }
}
