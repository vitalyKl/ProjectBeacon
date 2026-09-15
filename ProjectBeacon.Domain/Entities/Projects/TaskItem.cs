namespace ProjectBeacon.Domain.Entities.Projects;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Enums;

public class TaskItem : Entity, IProjectScoped
{
    public TaskItem() { }

    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public TaskItemStatus Status { get; private set; }
    public TaskPriority Priority { get; private set; }
    public TaskType Type { get; private set; }
    public TaskSubStage SubStage { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? LabelId { get; private set; }
    public Guid? MilestoneId { get; private set; }
    public string? ReviewNotes { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public TaskPipelineStage? PipelineStage { get; private set; }

    public Project Project { get; private set; } = null!;
    public Label? Label { get; private set; }
    public Milestone? Milestone { get; private set; }
    public ICollection<TaskComment> Comments { get; private set; } = [];
    public ICollection<TaskDependency> Dependencies { get; private set; } = [];
    public ICollection<Subtask> Subtasks { get; private set; } = [];

    public static TaskItem Create(string title, Guid projectId, TaskPriority priority = TaskPriority.Medium, TaskType type = TaskType.Task)
    {
        var task = Entity.New<TaskItem>();
        task.Title = title;
        task.ProjectId = projectId;
        task.Status = TaskItemStatus.Todo;
        task.Priority = priority;
        task.Type = type;
        task.CreatedAt = DateTime.UtcNow;
        return task;
    }

    public void Update(string? title = null, string? description = null, TaskPriority? priority = null, TaskType? type = null)
    {
        if (title is not null) Title = title;
        if (description is not null) Description = description;
        if (priority is not null) Priority = priority.Value;
        if (type is not null) Type = type.Value;
    }

    public void AssignLabel(Guid? labelId)
    {
        LabelId = labelId;
    }

    public void SetMilestone(Guid? milestoneId)
    {
        MilestoneId = milestoneId;
    }

    public void MoveToNextStatus()
    {
        Status = Status switch
        {
            TaskItemStatus.Todo => TaskItemStatus.InProgress,
            TaskItemStatus.InProgress => MoveInProgressToDone(),
            TaskItemStatus.Done => TaskItemStatus.Done,
            _ => throw new InvalidOperationException($"Unknown status: {Status}")
        };

        if (Status == TaskItemStatus.Done)
            CompletedAt = DateTime.UtcNow;
    }

    private TaskItemStatus MoveInProgressToDone()
    {
        if (string.IsNullOrWhiteSpace(ReviewNotes))
            throw new InvalidOperationException(
                "Cannot move task to Done without review notes. " +
                "Use SetReviewNotes() to provide review information, or " +
                "complete the sub-stage workflow via MoveToSubStage(TaskSubStage.Complete).");

        return TaskItemStatus.Done;
    }

    public void MoveToSubStage(TaskSubStage subStage)
    {
        if (Status != TaskItemStatus.InProgress)
            throw new InvalidOperationException("Sub-stage can only be changed when status is InProgress");

        SubStage = subStage;

        if (subStage == TaskSubStage.Complete)
        {
            Status = TaskItemStatus.Done;
            CompletedAt = DateTime.UtcNow;
        }
    }

    public void SetReviewNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            throw new ArgumentException("Review notes cannot be empty when moving to review.", nameof(notes));

        ReviewNotes = notes;
    }

    public void ResetToTodo()
    {
        Status = TaskItemStatus.Todo;
        CompletedAt = null;
    }

    public void TransitionTo(TaskItemStatus target)
    {
        if (Status == target)
            return;

        switch (target)
        {
            case TaskItemStatus.Todo:
                ResetToTodo();
                return;
            case TaskItemStatus.InProgress:
                if (Status == TaskItemStatus.Done)
                    ResetToTodo();
                if (Status == TaskItemStatus.Todo)
                    MoveToNextStatus();
                return;
            case TaskItemStatus.Done:
                if (string.IsNullOrWhiteSpace(ReviewNotes))
                    throw new InvalidOperationException(
                        "Cannot move task to Done without review notes. " +
                        "Use SetReviewNotes() to provide review information, or " +
                        "complete the sub-stage workflow via MoveToSubStage(TaskSubStage.Complete).");
                if (Status == TaskItemStatus.Todo)
                    MoveToNextStatus();
                MoveToNextStatus();
                return;
            default:
                throw new InvalidOperationException($"Unknown status: {target}");
        }
    }

    private string StageName => PipelineStage?.ToString() ?? TaskPipelineStage.None.ToString();

    public void StartPipeline()
    {
        if (PipelineStage is not null)
            throw new InvalidOperationException($"Pipeline is already started (stage: {StageName})");
        PipelineStage = TaskPipelineStage.Planning;
        TransitionTo(TaskItemStatus.InProgress);
    }

    public void EnterExecuting()
    {
        if (PipelineStage == TaskPipelineStage.Executing)
            return;
        if (PipelineStage != TaskPipelineStage.Planning)
            throw new InvalidOperationException($"Cannot enter executing from stage {StageName}");
        PipelineStage = TaskPipelineStage.Executing;
        TransitionTo(TaskItemStatus.InProgress);
    }

    public void EnterReview()
    {
        if (PipelineStage != TaskPipelineStage.Executing)
            throw new InvalidOperationException($"Cannot enter review from stage {StageName}");
        PipelineStage = TaskPipelineStage.Reviewing;
    }

    public void SetApproved()
    {
        if (PipelineStage != TaskPipelineStage.Reviewing)
            throw new InvalidOperationException($"Cannot approve from stage {StageName}");
        PipelineStage = TaskPipelineStage.Approved;
        // D1: Approved maps to the board as Done; the pipeline review replaces the cold-diff gate here.
        if (Status != TaskItemStatus.Done)
        {
            Status = TaskItemStatus.Done;
            CompletedAt ??= DateTime.UtcNow;
        }
    }

    public void ReopenForRevision()
    {
        if (PipelineStage != TaskPipelineStage.Approved)
            throw new InvalidOperationException($"Cannot reopen for revision from stage {StageName}");
        PipelineStage = TaskPipelineStage.ReopenedForRevision;
        TransitionTo(TaskItemStatus.InProgress);
    }

    public void ClosePipeline(string reviewNotes)
    {
        if (string.IsNullOrWhiteSpace(reviewNotes))
            throw new ArgumentException("Review notes cannot be empty when closing the pipeline.", nameof(reviewNotes));
        if (PipelineStage is not (TaskPipelineStage.Approved or TaskPipelineStage.ReopenedForRevision))
            throw new InvalidOperationException($"Cannot close pipeline from stage {StageName}");
        ReviewNotes = reviewNotes;
        PipelineStage = TaskPipelineStage.Closed;
        if (Status != TaskItemStatus.Done)
            TransitionTo(TaskItemStatus.Done);
        else
            CompletedAt ??= DateTime.UtcNow;
    }
}
