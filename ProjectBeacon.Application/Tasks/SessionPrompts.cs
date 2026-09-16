namespace ProjectBeacon.Application.Tasks;

using System.Text;
using Domain.Entities.Projects;
using Domain.Enums;

/// <summary>
/// PromptContext builders (FR-B1): each role only gets the context it needs.
/// Actor sees just its subtask instructions + scope; review sees only final artifacts.
/// </summary>
public static class SessionPrompts
{
    public static string Planner(TaskItem task, string? projectName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Role: planner. You plan the work; you do not implement it.");
        sb.AppendLine($"Project: {(string.IsNullOrWhiteSpace(projectName) ? "(unnamed)" : projectName)}");
        sb.AppendLine($"Task: {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            sb.AppendLine();
            sb.AppendLine("Description:");
            sb.AppendLine(task.Description);
        }
        sb.AppendLine();
        sb.AppendLine("Inspect the codebase with your file tools, then split the work into small, independently executable subtasks.");
        sb.AppendLine("Create each subtask via the pipeline API with focused instructions and, where useful, allowed MCP tools and paths.");
        return sb.ToString().TrimEnd();
    }

    public static string Actor(Subtask subtask)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Role: actor. Execute exactly this subtask; you are not told the surrounding task.");
        sb.AppendLine();
        sb.AppendLine("Instructions:");
        sb.AppendLine(subtask.Instructions);
        var tools = subtask.AllowedMcpTools;
        var paths = subtask.AllowedPaths;
        if (tools.Count > 0 || paths.Count > 0)
            sb.AppendLine();
        if (tools.Count > 0)
            sb.AppendLine($"Allowed MCP tools: {string.Join(", ", tools)}");
        if (paths.Count > 0)
            sb.AppendLine($"Allowed paths: {string.Join(", ", paths)}");
        sb.AppendLine();
        sb.AppendLine("When the subtask is done, report a diff reference and a short summary via the pipeline API.");
        return sb.ToString().TrimEnd();
    }

    public static string Review(TaskItem task, IReadOnlyList<Subtask> subtasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Role: reviewer. Review the artifacts below; you do not see the planning or actor transcripts.");
        sb.AppendLine($"Task: {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            sb.AppendLine();
            sb.AppendLine("Description:");
            sb.AppendLine(task.Description);
        }
        sb.AppendLine();
        foreach (var sub in subtasks)
        {
            sb.AppendLine($"{(sub.Status == SubtaskStatus.Done ? "[DONE]" : "[FAILED - work not completed]")} subtask {sub.Id}");
            AppendIndented(sb, "  Instructions:", sub.Instructions);
            if (sub.DiffRef is not null)
                sb.AppendLine($"  DiffRef: {sub.DiffRef}");
            if (sub.Summary is not null)
                sb.AppendLine($"  Summary: {sub.Summary}");
        }
        sb.AppendLine();
        sb.AppendLine("Record a verdict: approve, or reopen one subtask with a concrete fix note.");
        return sb.ToString().TrimEnd();
    }

    private static void AppendIndented(StringBuilder sb, string header, string value)
    {
        sb.AppendLine(header);
        foreach (var line in value.Split('\n'))
            sb.AppendLine("  " + line.TrimEnd('\r'));
    }
}
