namespace ProjectBeacon.Domain.Entities.Tasks;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Entities.Roadmap;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// Board task for one project. Status moves Todo, InProgress, Done.
/// Done requires review notes, unless <see cref="MoveToSubStage"/> is called with <see cref="TaskSubStage.Complete"/>.
/// The type is named <c>TaskItem</c> so it does not collide with <c>System.Threading.Tasks.Task</c>.
/// </summary>
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

    /// <summary>
    /// Optimistic-concurrency token for guarded state transitions. Guarded finish
    /// transitions compare-and-swap it in SQL, so a rival report that read a stale
    /// row is rejected at the database even when its status guard still matches.
    /// </summary>
    public int StateRevision { get; private set; }

    /// <summary>Optimistic-concurrency token for Description and ReviewNotes content only.</summary>
    public int ContentRevision { get; private set; } = 1;

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
        if (description is not null && !string.Equals(Description, description, StringComparison.Ordinal))
        {
            Description = description;
            ContentRevision++;
        }
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

    /// <summary>
    /// Advances one step. InProgress to Done throws when <see cref="ReviewNotes"/> is missing.
    /// Done stays Done.
    /// </summary>
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

    /// <summary>
    /// Sets the in-progress sub-stage. Throws unless status is InProgress.
    /// <see cref="TaskSubStage.Complete"/> also marks the task Done and sets <see cref="CompletedAt"/>.
    /// </summary>
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

    /// <summary>Stores review notes. Throws when <paramref name="notes"/> is empty.</summary>
    public void SetReviewNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            throw new ArgumentException("Review notes cannot be empty when moving to review.", nameof(notes));

        if (!string.Equals(ReviewNotes, notes, StringComparison.Ordinal))
        {
            ReviewNotes = notes;
            ContentRevision++;
        }
    }

    /// <summary>Returns the task to Todo and clears <see cref="CompletedAt"/>.</summary>
    public void ResetToTodo()
    {
        Status = TaskItemStatus.Todo;
        CompletedAt = null;
    }

    /// <summary>
    /// Moves to <paramref name="target"/>, stepping through the legal predecessors.
    /// Done throws when review notes are missing.
    /// </summary>
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

    /// <summary>Appends a system note to the task description and advances its content revision.</summary>
    public void AppendDescription(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("Description note cannot be empty.", nameof(note));
        var updated = string.IsNullOrEmpty(Description) ? note : Description + "\n\n" + note;
        if (!string.Equals(Description, updated, StringComparison.Ordinal))
        {
            Description = updated;
            ContentRevision++;
        }
    }

    private void ClearReviewNotes()
    {
        if (ReviewNotes is not null)
        {
            ReviewNotes = null;
            ContentRevision++;
        }
    }

    /// <summary>Advances the content stream when a revisioned task target is deleted.</summary>
    public void MarkContentDeleted() => ContentRevision++;

    private string StageName => PipelineStage?.ToString() ?? TaskPipelineStage.None.ToString();

    /// <summary>Starts the pipeline at Planning and moves the task to InProgress. Throws if a stage is already set.</summary>
    public void StartPipeline()
    {
        if (PipelineStage is not null)
            throw new InvalidOperationException($"Pipeline is already started (stage: {StageName})");
        PipelineStage = TaskPipelineStage.Planning;
        TransitionTo(TaskItemStatus.InProgress);
    }

    /// <summary>Enters Executing from Planning or ReopenedForRevision.</summary>
    public void EnterExecuting()
    {
        if (PipelineStage == TaskPipelineStage.Executing)
            return;
        if (PipelineStage is not (TaskPipelineStage.Planning or TaskPipelineStage.ReopenedForRevision))
            throw new InvalidOperationException($"Cannot enter executing from stage {StageName}");
        PipelineStage = TaskPipelineStage.Executing;
        TransitionTo(TaskItemStatus.InProgress);
    }

    /// <summary>Enters Reviewing. Allowed only from Executing.</summary>
    public void EnterReview()
    {
        if (PipelineStage != TaskPipelineStage.Executing)
            throw new InvalidOperationException($"Cannot enter review from stage {StageName}");
        PipelineStage = TaskPipelineStage.Reviewing;
    }

    /// <summary>Marks the pipeline Approved. Allowed only from Reviewing.</summary>
    public void SetApproved()
    {
        if (PipelineStage != TaskPipelineStage.Reviewing)
            throw new InvalidOperationException($"Cannot approve from stage {StageName}");
        PipelineStage = TaskPipelineStage.Approved;
    }

    /// <summary>Returns an Approved pipeline to ReopenedForRevision and the task to InProgress.</summary>
    public void ReopenForRevision()
    {
        if (PipelineStage != TaskPipelineStage.Approved)
            throw new InvalidOperationException($"Cannot reopen for revision from stage {StageName}");
        PipelineStage = TaskPipelineStage.ReopenedForRevision;
        TransitionTo(TaskItemStatus.InProgress);
    }

    /// <summary>
    /// Closes from Approved or ReopenedForRevision, stores <paramref name="reviewNotes"/>, and moves the task to Done.
    /// </summary>
    public void ClosePipeline(string reviewNotes)
    {
        if (string.IsNullOrWhiteSpace(reviewNotes))
            throw new ArgumentException("Review notes cannot be empty when closing the pipeline.", nameof(reviewNotes));
        if (PipelineStage is not (TaskPipelineStage.Approved or TaskPipelineStage.ReopenedForRevision))
            throw new InvalidOperationException($"Cannot close pipeline from stage {StageName}");
        SetReviewNotes(reviewNotes);
        PipelineStage = TaskPipelineStage.Closed;
        if (Status != TaskItemStatus.Done)
            TransitionTo(TaskItemStatus.Done);
        else
            CompletedAt ??= DateTime.UtcNow;
    }
    /// <summary>Stops the current pipeline without marking the task complete.</summary>
    public void CancelPipeline()
    {
        if (PipelineStage is null or TaskPipelineStage.None)
            throw new InvalidOperationException("Pipeline is not started.");
        if (PipelineStage == TaskPipelineStage.Closed)
            throw new InvalidOperationException("A closed pipeline cannot be cancelled. Restart it to begin again.");

        PipelineStage = TaskPipelineStage.Cancelled;
        Status = TaskItemStatus.Todo;
        SubStage = default;
        ClearReviewNotes();
        CompletedAt = null;
        StateRevision++;
    }

    /// <summary>Clears pipeline output so the task can start again from Planning.</summary>
    public void ResetPipeline()
    {
        PipelineStage = null;
        Status = TaskItemStatus.Todo;
        SubStage = default;
        ClearReviewNotes();
        CompletedAt = null;
        StateRevision++;
    }

}
