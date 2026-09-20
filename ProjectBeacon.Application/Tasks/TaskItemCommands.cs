namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Enums;

public record CreateTaskRequest(
    string Title,
    string? Description,
    Guid ProjectId,
    TaskPriority Priority,
    TaskType Type,
    Guid? LabelId,
    Guid? MilestoneId,
    string? Path = null);

public record CreateTaskCommand(CreateTaskRequest Request) : ICommand<Result<TaskItemDto>>;

public record UpdateTaskRequest(
    Guid TaskId,
    string? Title,
    string? Description,
    TaskPriority? Priority,
    TaskType? Type,
    Guid? LabelId,
    Guid? MilestoneId);

public record UpdateTaskCommand(UpdateTaskRequest Request) : ICommand<Result<TaskItemDto>>;

public record DeleteTaskRequest(Guid TaskId);

public record DeleteTaskCommand(DeleteTaskRequest Request) : ICommand<Result<bool>>;

public record ChangeSubStageRequest(Guid TaskId, TaskSubStage SubStage);

public record ChangeSubStageCommand(ChangeSubStageRequest Request) : ICommand<Result<TaskItemDto>>;

public record AddCommentRequest(Guid TaskId, string Content, Guid UserId = default);

public record AddCommentCommand(AddCommentRequest Request) : ICommand<Result<TaskCommentDto>>;

public record SetDependenciesRequest(Guid TaskId, IList<Guid> DependentTaskIds);

public record SetDependenciesCommand(SetDependenciesRequest Request) : ICommand<Result<TaskItemDto>>;

public record AddReviewNotesRequest(Guid TaskId, string ReviewNotes);

public record AddReviewNotesCommand(AddReviewNotesRequest Request) : ICommand<Result<TaskItemDto>>;

public record ChangeTaskStatusRequest(Guid TaskId, TaskItemStatus Status, Guid? ProjectId = null);

public record ChangeTaskStatusCommand(ChangeTaskStatusRequest Request) : ICommand<Result<TaskItemDto>>;

public record FinishWorkReview(bool ReviewerRun, int RegressionsFound, int RegressionsFixed);

public record FinishWorkRequest(
    string TaskId,
    string Result,
    string? Output,
    string ActorId,
    FinishWorkReview? Review = null);

public record FinishWorkCommand(FinishWorkRequest Request) : ICommand<Result<bool>>;

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

public record TaskCommentDto(
    Guid Id,
    string Content,
    Guid UserId,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record TaskStepDto(Guid Id, Guid TaskId, string Title, int SortOrder, DateTime? DoneAt, bool IsDone);

public record ListTaskStepsRequest(Guid TaskId);
public record ListTaskStepsCommand(ListTaskStepsRequest Request) : ICommand<Result<IList<TaskStepDto>>>;

public record AddTaskStepRequest(Guid TaskId, string Title);
public record AddTaskStepCommand(AddTaskStepRequest Request) : ICommand<Result<TaskStepDto>>;

public record ToggleTaskStepRequest(Guid StepId, bool Done);
public record ToggleTaskStepCommand(ToggleTaskStepRequest Request) : ICommand<Result<TaskStepDto>>;

public record DeleteTaskStepRequest(Guid StepId);
public record DeleteTaskStepCommand(DeleteTaskStepRequest Request) : ICommand<Result<bool>>;
