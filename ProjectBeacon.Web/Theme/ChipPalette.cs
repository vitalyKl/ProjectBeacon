using MudBlazor;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Theme;

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
}
