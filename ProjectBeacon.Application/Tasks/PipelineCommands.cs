namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Enums;

public record SubtaskDto(
    Guid Id,
    string Instructions,
    SubtaskStatus Status,
    string? DiffRef,
    string? Summary,
    int ReopenCount,
    IReadOnlyList<string> AllowedMcpTools,
    IReadOnlyList<string> AllowedPaths,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record PipelineSessionDto(
    Guid Id,
    Guid TaskId,
    Guid? SubtaskId,
    PipelineRole Role,
    SessionStatus Status,
    Guid? ModelBackendId,
    string? LaunchSpec,
    string PromptContext,
    DateTime? LaunchedAt,
    DateTime? ClosedAt);

public record ReviewVerdictDto(
    Guid Id,
    Guid TaskId,
    Guid? SubtaskId,
    ReviewVerdictKind Kind,
    string Note,
    DateTime CreatedAt);

public record PipelineStateDto(
    Guid TaskId,
    string Title,
    TaskItemStatus Status,
    TaskPipelineStage? Stage,
    string? ReviewNotes,
    IReadOnlyList<SubtaskDto> Subtasks,
    IReadOnlyList<PipelineSessionDto> Sessions,
    IReadOnlyList<ReviewVerdictDto> Verdicts);

public record StartPipelineRequest(Guid TaskId);

public record StartPipelineCommand(StartPipelineRequest Request) : ICommand<Result<PipelineSessionDto>>;

public record CreateSubtaskRequest(
    Guid TaskId,
    string Instructions,
    IReadOnlyList<string>? AllowedMcpTools = null,
    IReadOnlyList<string>? AllowedPaths = null);

public record CreateSubtaskCommand(CreateSubtaskRequest Request) : ICommand<Result<SubtaskDto>>;

public record StartActorSessionRequest(Guid TaskId, Guid SubtaskId);

public record StartActorSessionCommand(StartActorSessionRequest Request) : ICommand<Result<PipelineSessionDto>>;

public record LaunchSessionRequest(Guid SessionId);

public record LaunchSessionCommand(LaunchSessionRequest Request) : ICommand<Result<PipelineSessionDto>>;

public record ReportSubtaskResultRequest(Guid TaskId, Guid SubtaskId, string DiffRef, string Summary);

public record ReportSubtaskResultCommand(ReportSubtaskResultRequest Request) : ICommand<Result<PipelineStateDto>>;

public record FailSubtaskRequest(Guid TaskId, Guid SubtaskId, string Reason);

public record FailSubtaskCommand(FailSubtaskRequest Request) : ICommand<Result<PipelineStateDto>>;

public record StartReviewRequest(Guid TaskId);

public record StartReviewCommand(StartReviewRequest Request) : ICommand<Result<PipelineSessionDto>>;

public record RecordReviewVerdictRequest(Guid TaskId, ReviewVerdictKind Kind, string Note, Guid? SubtaskId = null);

public record RecordReviewVerdictCommand(RecordReviewVerdictRequest Request) : ICommand<Result<PipelineStateDto>>;

public record ApprovePipelineRequest(Guid TaskId, string? Note = null);

public record ApprovePipelineCommand(ApprovePipelineRequest Request) : ICommand<Result<PipelineStateDto>>;

public record ForceClosePipelineRequest(Guid TaskId, string ActorId, string? Reason = null);

public record ForceClosePipelineCommand(ForceClosePipelineRequest Request) : ICommand<Result<PipelineStateDto>>;

public record GetPipelineRequest(Guid TaskId);

public record GetPipelineCommand(GetPipelineRequest Request) : ICommand<Result<PipelineStateDto>>;
