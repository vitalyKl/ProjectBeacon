namespace ProjectBeacon.Application.Tasks;

using Application.Authorization;
using Application.Common;
using Domain.Enums;
/// <summary>
/// Title, project, priority, and type. Label, milestone, path, and kind are optional.
/// </summary>
public record CreateTaskRequest(
    string Title,
    string? Description,
    Guid ProjectId,
    TaskPriority Priority,
    TaskType Type,
    Guid? LabelId,
    Guid? MilestoneId,
    string? Path = null,
    Guid? KindId = null);
/// <summary>
/// Command for create task.
/// </summary>
public record CreateTaskCommand(CreateTaskRequest Request, ActorContext Actor) : ICommand<Result<TaskItemDto>>;
/// <summary>
/// Fields for update task.
/// </summary>
public record UpdateTaskRequest(
    Guid TaskId,
    string? Title,
    string? Description,
    TaskPriority? Priority,
    TaskType? Type,
    Guid? LabelId,
    Guid? MilestoneId);
/// <summary>
/// Command for update task.
/// </summary>
public record UpdateTaskCommand(UpdateTaskRequest Request) : ICommand<Result<TaskItemDto>>;
/// <summary>
/// Fields for delete task.
/// </summary>
public record DeleteTaskRequest(Guid TaskId);
/// <summary>
/// Command for delete task.
/// </summary>
public record DeleteTaskCommand(DeleteTaskRequest Request) : ICommand<Result>;
/// <summary>
/// Task and the in-progress sub-stage. The domain rejects this unless the task is InProgress.
/// </summary>
public record ChangeSubStageRequest(Guid TaskId, TaskSubStage SubStage);
/// <summary>
/// Command for change sub stage.
/// </summary>
public record ChangeSubStageCommand(ChangeSubStageRequest Request) : ICommand<Result<TaskItemDto>>;
/// <summary>
/// Task and comment text. The author is the caller on the command, not a field here.
/// </summary>
public record AddCommentRequest(Guid TaskId, string Content);
/// <summary>
/// Command for add comment.
/// </summary>
public record AddCommentCommand(AddCommentRequest Request, ActorContext Actor) : ICommand<Result<TaskCommentDto>>;
/// <summary>
/// Task and the tasks it depends on. A task cannot depend on itself.
/// </summary>
public record SetDependenciesRequest(Guid TaskId, IList<Guid> DependentTaskIds);
/// <summary>
/// Command for set dependencies.
/// </summary>
public record SetDependenciesCommand(SetDependenciesRequest Request) : ICommand<Result<TaskItemDto>>;
/// <summary>
/// Task and the review notes. Empty notes are rejected.
/// </summary>
public record AddReviewNotesRequest(Guid TaskId, string ReviewNotes);
/// <summary>
/// Command for add review notes.
/// </summary>
public record AddReviewNotesCommand(AddReviewNotesRequest Request) : ICommand<Result<TaskItemDto>>;
/// <summary>
/// Task and the target status. Done fails unless a completed review run exists.
/// </summary>
public record ChangeTaskStatusRequest(Guid TaskId, TaskItemStatus Status, Guid? ProjectId = null);
/// <summary>
/// Command for change task status.
/// </summary>
public record ChangeTaskStatusCommand(ChangeTaskStatusRequest Request) : ICommand<Result<TaskItemDto>>;
/// <summary>
/// Optional review counts. ReviewerRun alone is not proof that the work is done.
/// </summary>
public record FinishWorkReview(bool ReviewerRun, int RegressionsFound, int RegressionsFixed);
/// <summary>
/// Result is done, failed, skipped, or partial. ActorId is the principal user id supplied by the host, not a client body field. Done requires ReviewRunId.
/// </summary>
public record FinishWorkRequest(
    string TaskId,
    string Result,
    string? Output,
    string ActorId,
    FinishWorkReview? Review = null,
    string? ReviewTranscriptRef = null,
    Guid? ReviewRunId = null);
/// <summary>
/// Command for finish work.
/// </summary>
public record FinishWorkCommand(FinishWorkRequest Request) : ICommand<Result>;
/// <summary>
/// A task row, including status, priority, review notes, and timestamps.
/// </summary>
public record TaskItemDto(
    Guid Id,
    string Title,
    string? Description,
    string Status,
    TaskPriority Priority,
    TaskType Type,
    TaskSubStage? SubStage,
    Guid ProjectId,
    Guid? LabelId,
    Guid? MilestoneId,
    string? ReviewNotes,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    IList<TaskCommentDto> Comments,
    IList<Guid> Dependencies);
/// <summary>
/// A comment, its author, and timestamps.
/// </summary>
public record TaskCommentDto(
    Guid Id,
    string Content,
    Guid UserId,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
/// <summary>
/// A checklist step. IsDone follows DoneAt.
/// </summary>
public record TaskStepDto(Guid Id, Guid TaskId, string Title, int SortOrder, DateTime? DoneAt, bool IsDone);
/// <summary>
/// Fields for list task steps.
/// </summary>
public record ListTaskStepsRequest(Guid TaskId);
/// <summary>
/// Command for list task steps.
/// </summary>
public record ListTaskStepsCommand(ListTaskStepsRequest Request) : ICommand<Result<IList<TaskStepDto>>>;
/// <summary>
/// Fields for add task step.
/// </summary>
public record AddTaskStepRequest(Guid TaskId, string Title);
/// <summary>
/// Command for add task step.
/// </summary>
public record AddTaskStepCommand(AddTaskStepRequest Request) : ICommand<Result<TaskStepDto>>;
/// <summary>
/// Step id and whether it is done.
/// </summary>
public record ToggleTaskStepRequest(Guid StepId, bool Done);
/// <summary>
/// Command for toggle task step.
/// </summary>
public record ToggleTaskStepCommand(ToggleTaskStepRequest Request) : ICommand<Result<TaskStepDto>>;
/// <summary>
/// Fields for delete task step.
/// </summary>
public record DeleteTaskStepRequest(Guid StepId);
/// <summary>
/// Command for delete task step.
/// </summary>
public record DeleteTaskStepCommand(DeleteTaskStepRequest Request) : ICommand<Result>;
