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
    /// <summary>Published Release from whose verified deployed commit this Hotfix was created.</summary>
    public Guid? HotfixSourceReleaseId { get; private set; }
    public string? HotfixDeployedSha { get; private set; }
    public string? HotfixTargetBranch { get; private set; }
    /// <summary>Idempotency key supplied by the human who creates the Hotfix task.</summary>
    public Guid? HotfixRequestId { get; private set; }
    /// <summary>Set on the ordinary follow-up task automatically created after the Hotfix lands.</summary>
    public Guid? ForwardPortOfTaskId { get; private set; }
    public string? ReviewNotes { get; private set; }
    /// <summary>Agent-declared version impact for this task, confirmed or challenged by Reviewer.</summary>
    public string? VersionImpact { get; private set; }
    public string? VersionImpactRationale { get; private set; }
    public DateTime? VersionImpactAt { get; private set; }
    public string? VersionImpactReviewStatus { get; private set; }
    public string? VersionImpactReviewNote { get; private set; }
    public DateTime? VersionImpactReviewedAt { get; private set; }
    public Guid? VersionImpactReviewedBySessionId { get; private set; }
    public int VersionImpactRevision { get; private set; } = 1;
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public TaskPipelineStage? PipelineStage { get; private set; }

    /// <summary>Workstation that owns this task's persistent local branch workspace.</summary>
    public Guid? PipelineDeviceId { get; private set; }

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

    public static TaskItem Create(string title, Guid projectId, TaskPriority priority = TaskPriority.Medium, TaskType type = TaskType.Task, string? description = null)
    {
        var task = Entity.New<TaskItem>();
        task.Title = title;
        task.ProjectId = projectId;
        task.Status = TaskItemStatus.Todo;
        task.Priority = priority;
        task.Type = type;
        task.Description = description;
        task.CreatedAt = DateTime.UtcNow;
        return task;
    }

    public void Update(string? title = null, string? description = null, TaskPriority? priority = null, TaskType? type = null)
    {
        var scopeChanged =
            (title is not null && !string.Equals(Title, title, StringComparison.Ordinal))
            || (description is not null && !string.Equals(Description, description, StringComparison.Ordinal));

        if (title is not null) Title = title;
        if (description is not null && !string.Equals(Description, description, StringComparison.Ordinal))
        {
            Description = description;
            ContentRevision++;
        }
        if (priority is not null) Priority = priority.Value;
        if (type is not null) Type = type.Value;
        if (scopeChanged && VersionImpact is not null)
            ClearVersionImpact();
    }

    /// <summary>Restores nullable description and review notes from a historical content snapshot.</summary>
    public void RestoreContentFromHistory(string? description, string? reviewNotes)
    {
        if (string.Equals(Description, description, StringComparison.Ordinal)
            && string.Equals(ReviewNotes, reviewNotes, StringComparison.Ordinal))
            return;

        var scopeChanged = !string.Equals(Description, description, StringComparison.Ordinal);
        Description = description;
        ReviewNotes = reviewNotes;
        ContentRevision++;
        if (scopeChanged && VersionImpact is not null)
            ClearVersionImpact();
    }

    /// <summary>Pins the task's local pipeline workspace to one workstation. The pin is permanent unless an explicit migration is performed.</summary>
    public void PinPipelineDevice(Guid deviceId)
    {
        if (deviceId == Guid.Empty)
            throw new ArgumentException("Pipeline workstation id is required.", nameof(deviceId));
        if (PipelineDeviceId is { } existing && existing != deviceId)
            throw new InvalidOperationException("The task is already pinned to another workstation. Migrate its task workspace explicitly before changing the pin.");
        PipelineDeviceId = deviceId;
    }

    /// <summary>Marks this task as a Hotfix based on an exact verified production deployment.</summary>
    public void SetHotfixOrigin(Guid releaseId, string deployedSha, string targetBranch, Guid requestId)
    {
        if (releaseId == Guid.Empty || requestId == Guid.Empty)
            throw new ArgumentException("Published Release and Hotfix request ids are required.");
        if (deployedSha is not { Length: 40 or 64 } || !deployedSha.All(Uri.IsHexDigit))
            throw new ArgumentException("Hotfix deployed commit must be a full Git SHA.", nameof(deployedSha));
        if (!ProjectVersionPolicyCatalog.IsSafeBranchName(targetBranch))
            throw new ArgumentException("Hotfix target branch is not a safe Git branch name.", nameof(targetBranch));
        if (HotfixSourceReleaseId is not null || HotfixRequestId is not null)
            throw new InvalidOperationException("Hotfix origin is immutable once assigned.");

        HotfixSourceReleaseId = releaseId;
        HotfixDeployedSha = deployedSha.ToLowerInvariant();
        HotfixTargetBranch = targetBranch;
        HotfixRequestId = requestId;
    }

    /// <summary>Links the ordinary post-landing forward-port task to its source Hotfix once.</summary>
    public void SetForwardPortOfTask(Guid hotfixTaskId)
    {
        if (hotfixTaskId == Guid.Empty || hotfixTaskId == Id)
            throw new ArgumentException("A different source Hotfix task is required.", nameof(hotfixTaskId));
        if (HotfixSourceReleaseId is not null)
            throw new InvalidOperationException("A Hotfix cannot itself be a forward-port task.");
        if (ForwardPortOfTaskId is not null && ForwardPortOfTaskId != hotfixTaskId)
            throw new InvalidOperationException("The source Hotfix of a forward-port task is immutable.");
        ForwardPortOfTaskId = hotfixTaskId;
    }

    /// <summary>Stores the agent's proposed version impact; publication gates remain server-owned.</summary>
    public void SetVersionImpact(string impact, string rationale)
    {
        if (impact is not ("none" or "patch" or "minor" or "major"))
            throw new ArgumentException("Version impact must be none, patch, minor, or major.", nameof(impact));
        ArgumentException.ThrowIfNullOrWhiteSpace(rationale);
        if (rationale.Length > 1000)
            throw new ArgumentException("Version impact rationale cannot exceed 1000 characters.", nameof(rationale));
        VersionImpact = impact;
        VersionImpactRationale = rationale.Trim();
        VersionImpactAt = DateTime.UtcNow;
        VersionImpactReviewStatus = null;
        VersionImpactReviewNote = null;
        VersionImpactReviewedAt = null;
        VersionImpactReviewedBySessionId = null;
        VersionImpactRevision++;
    }

    /// <summary>Records a Reviewer decision for the current impact revision.</summary>
    public void ReviewVersionImpact(string decision, string note, Guid reviewSessionId)
    {
        if (VersionImpact is null)
            throw new InvalidOperationException("A task must declare version impact before it can be reviewed.");
        if (decision is not ("Confirmed" or "Challenged"))
            throw new ArgumentException("Version impact review decision must be Confirmed or Challenged.", nameof(decision));
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        if (note.Trim().Length > 1000)
            throw new ArgumentException("Version impact review note cannot exceed 1000 characters.", nameof(note));
        if (reviewSessionId == Guid.Empty)
            throw new ArgumentException("Reviewer session id is required.", nameof(reviewSessionId));

        VersionImpactReviewStatus = decision;
        VersionImpactReviewNote = note.Trim();
        VersionImpactReviewedAt = DateTime.UtcNow;
        VersionImpactReviewedBySessionId = reviewSessionId;
        VersionImpactRevision++;
    }

    /// <summary>Clears a declaration when a task is reopened for a materially different change.</summary>
    public void ClearVersionImpact()
    {
        VersionImpact = null;
        VersionImpactRationale = null;
        VersionImpactAt = null;
        VersionImpactReviewStatus = null;
        VersionImpactReviewNote = null;
        VersionImpactReviewedAt = null;
        VersionImpactReviewedBySessionId = null;
        VersionImpactRevision++;
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
