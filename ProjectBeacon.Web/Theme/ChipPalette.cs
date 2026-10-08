using MudBlazor;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Theme;
/// <summary>
/// MudBlazor colors for status and priority chips. Status is not color alone. ChromeLabels supplies the text.
/// </summary>
public static class ChipPalette
{
    public static Color ForStatus(string? status) => status switch
    {
        "Done" => Color.Success,
        "InProgress" => Color.Info,
        _ => Color.Default
    };

    public static Color ForPriority(TaskPriority priority) => priority switch
    {
        TaskPriority.Critical => Color.Error,
        TaskPriority.High => Color.Warning,
        _ => Color.Default
    };

    public static Color ForType(TaskType type) => type switch
    {
        TaskType.Bug => Color.Error,
        _ => Color.Default
    };

    public static Color ForDecisionStatus(DecisionStatus status) => status switch
    {
        DecisionStatus.Accepted => Color.Success,
        DecisionStatus.Deprecated => Color.Warning,
        DecisionStatus.Superseded => Color.Default,
        _ => Color.Info
    };

    public static Color ForConstraintStatus(ConstraintStatus status) => status switch
    {
        ConstraintStatus.Active => Color.Success,
        ConstraintStatus.Rejected => Color.Error,
        _ => Color.Info
    };

    public static Color ForRuntime(string? state) => state switch
    {
        "Ready" or "Online" or "Succeeded" => Color.Success,
        "Busy" or "Running" or "Starting" or "Stopping" or "Queued" => Color.Info,
        "Degraded" => Color.Warning,
        "Failed" => Color.Error,
        _ => Color.Default
    };

    public static Color ForCommandState(string? state) => state switch
    {
        "Succeeded" => Color.Success,
        "Running" or "Queued" or "Pending" => Color.Info,
        "Failed" or "TimedOut" => Color.Error,
        "Cancelled" => Color.Warning,
        _ => Color.Default
    };

    public static Color ForCommandStatus(WorkstationCommandStatus status) => status switch
    {
        WorkstationCommandStatus.Succeeded => Color.Success,
        WorkstationCommandStatus.Running => Color.Info,
        WorkstationCommandStatus.Pending => Color.Default,
        WorkstationCommandStatus.Failed => Color.Error,
        WorkstationCommandStatus.Cancelled => Color.Warning,
        _ => Color.Default
    };
}
