using ProjectBeacon.Application.Tasks;

namespace ProjectBeacon.Web.Features.Tasks;
/// <summary>
/// Which pipeline dialog is open: create, report, fail, approve, reopen, close, or force-close.
/// </summary>
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
/// <summary>
/// Inputs for that dialog: mode, task, optional subtask, and the actor id the page already resolved.
/// </summary>
public record PipelineDialogParams(
    PipelineDialogMode Mode,
    Guid TaskId,
    SubtaskDto? Subtask = null,
    IReadOnlyList<SubtaskDto>? Subtasks = null,
    string? ActorId = null);
