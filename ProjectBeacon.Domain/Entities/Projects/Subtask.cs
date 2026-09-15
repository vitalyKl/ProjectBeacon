namespace ProjectBeacon.Domain.Entities.Projects;

using System.Text.Json;
using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class Subtask : Entity, IProjectScoped
{
    public Subtask() { }

    public Guid TaskId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Instructions { get; private set; } = string.Empty;
    public SubtaskStatus Status { get; private set; }
    public string? DiffRef { get; private set; }
    public string? Summary { get; private set; }
    public int ReopenCount { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    // EF maps the JSON text backing; the list views are ignored in the model (D3).
    public string AllowedMcpToolsJson { get; private set; } = "[]";
    public string AllowedPathsJson { get; private set; } = "[]";

    public IReadOnlyList<string> AllowedMcpTools
    {
        get
        {
            if (string.IsNullOrEmpty(AllowedMcpToolsJson))
                return [];
            try
            {
                return JsonSerializer.Deserialize<string[]>(AllowedMcpToolsJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
        private set => AllowedMcpToolsJson = JsonSerializer.Serialize(value ?? []);
    }

    public IReadOnlyList<string> AllowedPaths
    {
        get
        {
            if (string.IsNullOrEmpty(AllowedPathsJson))
                return [];
            try
            {
                return JsonSerializer.Deserialize<string[]>(AllowedPathsJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
        private set => AllowedPathsJson = JsonSerializer.Serialize(value ?? []);
    }

    public TaskItem Task { get; private set; } = null!;

    public static Subtask Create(
        string instructions,
        Guid taskId,
        Guid projectId,
        IReadOnlyList<string>? allowedMcpTools = null,
        IReadOnlyList<string>? allowedPaths = null)
    {
        var subtask = Entity.New<Subtask>();
        subtask.Instructions = instructions;
        subtask.TaskId = taskId;
        subtask.ProjectId = projectId;
        subtask.Status = SubtaskStatus.Pending;
        subtask.AllowedMcpTools = allowedMcpTools ?? [];
        subtask.AllowedPaths = allowedPaths ?? [];
        subtask.CreatedAt = DateTime.UtcNow;
        return subtask;
    }

    public void Start()
    {
        if (Status != SubtaskStatus.Pending)
            throw new InvalidOperationException($"Cannot start subtask in status {Status}");
        Status = SubtaskStatus.InProgress;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ReportResult(string diffRef, string summary)
    {
        if (Status != SubtaskStatus.InProgress)
            throw new InvalidOperationException($"Cannot report result for subtask in status {Status}");
        DiffRef = diffRef;
        Summary = summary;
        Status = SubtaskStatus.Done;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Fail(string? reason = null)
    {
        if (Status is SubtaskStatus.Done or SubtaskStatus.Failed)
            throw new InvalidOperationException($"Cannot fail subtask in status {Status}");
        Status = SubtaskStatus.Failed;
        if (reason is not null)
            Summary = reason;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Reopen(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("Reopen note cannot be empty.", nameof(note));
        if (Status is not (SubtaskStatus.Done or SubtaskStatus.Failed))
            throw new InvalidOperationException($"Cannot reopen subtask in status {Status}");
        Status = SubtaskStatus.Pending;
        Instructions += Environment.NewLine + note;
        ReopenCount++;
        UpdatedAt = DateTime.UtcNow;
    }
}
