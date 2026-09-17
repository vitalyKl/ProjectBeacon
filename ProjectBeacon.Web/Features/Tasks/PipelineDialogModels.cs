using ProjectBeacon.Application.Tasks;

namespace ProjectBeacon.Web.Features.Tasks;

public enum PipelineDialogMode
{
    CreateSubtask,
    ReportResult,
    FailSubtask,
    ReviewApprove,
    ReviewReopen,
    ConfirmClose,
    ForceClose
}

public record PipelineDialogParams(
    PipelineDialogMode Mode,
    Guid TaskId,
    SubtaskDto? Subtask = null,
    IReadOnlyList<SubtaskDto>? Subtasks = null,
    string? ActorId = null);
