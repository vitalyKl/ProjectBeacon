namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Domain.Enums;

/// <summary>Subtask row returned by the pipeline.</summary>
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
    DateTime? UpdatedAt,
    Guid? TaskPhaseId = null);

/// <summary>One pipeline session, including role, status, and prompt context.</summary>
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

/// <summary>A recorded review verdict for a task, optionally aimed at one subtask.</summary>
public record ReviewVerdictDto(
    Guid Id,
    Guid TaskId,
    Guid? SubtaskId,
    ReviewVerdictKind Kind,
    string Note,
    DateTime CreatedAt);

/// <summary>Task pipeline snapshot: stage, subtasks, sessions, and verdicts.</summary>
public record PipelineStateDto(
    Guid TaskId,
    string Title,
    TaskItemStatus Status,
    TaskPipelineStage? Stage,
    string? ReviewNotes,
    IReadOnlyList<SubtaskDto> Subtasks,
    IReadOnlyList<PipelineSessionDto> Sessions,
    IReadOnlyList<ReviewVerdictDto> Verdicts);

/// <summary>Identifies the task whose pipeline should start.</summary>
public record StartPipelineRequest(Guid TaskId);

/// <summary>Starts the pipeline for a task and returns the created session.</summary>
public record StartPipelineCommand(StartPipelineRequest Request) : ICommand<Result<PipelineSessionDto>>;

/// <summary>Instructions and optional tool and path limits for a new subtask.</summary>
public record CreateSubtaskRequest(
    Guid TaskId,
    string Instructions,
    IReadOnlyList<string>? AllowedMcpTools = null,
    IReadOnlyList<string>? AllowedPaths = null);

/// <summary>Creates a subtask on a task.</summary>
public record CreateSubtaskCommand(CreateSubtaskRequest Request) : ICommand<Result<SubtaskDto>>;

/// <summary>Identifies the subtask an actor session should run.</summary>
public record StartActorSessionRequest(Guid TaskId, Guid SubtaskId);

/// <summary>Starts an actor session for a subtask.</summary>
public record StartActorSessionCommand(StartActorSessionRequest Request) : ICommand<Result<PipelineSessionDto>>;

/// <summary>Identifies a pipeline session to launch.</summary>
public record LaunchSessionRequest(Guid SessionId);

/// <summary>Launches an existing pipeline session.</summary>
public record LaunchSessionCommand(LaunchSessionRequest Request) : ICommand<Result<PipelineSessionDto>>;

/// <summary>Diff reference and summary reported for a subtask.</summary>
public record ReportSubtaskResultRequest(Guid TaskId, Guid SubtaskId, string DiffRef, string Summary);

/// <summary>Records a subtask result and returns pipeline state.</summary>
public record ReportSubtaskResultCommand(ReportSubtaskResultRequest Request) : ICommand<Result<PipelineStateDto>>;

/// <summary>Reason a subtask failed.</summary>
public record FailSubtaskRequest(Guid TaskId, Guid SubtaskId, string Reason);

/// <summary>Marks a subtask failed and returns pipeline state.</summary>
public record FailSubtaskCommand(FailSubtaskRequest Request) : ICommand<Result<PipelineStateDto>>;

/// <summary>Identifies the task whose review stage should start.</summary>
public record StartReviewRequest(Guid TaskId);

/// <summary>Starts the review session for a task.</summary>
public record StartReviewCommand(StartReviewRequest Request) : ICommand<Result<PipelineSessionDto>>;

/// <summary>Verdict kind, note, and optional subtask for a review.</summary>
public record RecordReviewVerdictRequest(Guid TaskId, ReviewVerdictKind Kind, string Note, Guid? SubtaskId = null);

/// <summary>Records a review verdict and returns pipeline state.</summary>
public record RecordReviewVerdictCommand(RecordReviewVerdictRequest Request) : ICommand<Result<PipelineStateDto>>;

/// <summary>Device, actor, and check command used to record a review check.</summary>
public record RecordReviewCheckRequest(Guid TaskId, Guid DeviceId, Guid ActorId, string CheckCommand, string? Path = null);

/// <summary>Records a review check and returns pipeline state.</summary>
public record RecordReviewCheckCommand(RecordReviewCheckRequest Request) : ICommand<Result<PipelineStateDto>>;

/// <summary>Task to approve, with an optional note.</summary>
public record ApprovePipelineRequest(Guid TaskId, string? Note = null);

/// <summary>Approves a pipeline and returns its state.</summary>
public record ApprovePipelineCommand(ApprovePipelineRequest Request) : ICommand<Result<PipelineStateDto>>;

/// <summary>Task to force-close, the actor id, and an optional reason.</summary>
public record ForceClosePipelineRequest(Guid TaskId, string ActorId, string? Reason = null);

/// <summary>Force-closes a pipeline and returns its state.</summary>
public record ForceClosePipelineCommand(ForceClosePipelineRequest Request) : ICommand<Result<PipelineStateDto>>;

/// <summary>Identifies the task whose pipeline state is requested.</summary>
public record GetPipelineRequest(Guid TaskId);

/// <summary>Loads pipeline state for a task.</summary>
public record GetPipelineCommand(GetPipelineRequest Request) : ICommand<Result<PipelineStateDto>>;
