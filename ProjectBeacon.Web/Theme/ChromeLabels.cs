using Microsoft.Extensions.Localization;
using ProjectBeacon.Domain.Enums;

namespace ProjectBeacon.Web.Theme;

public static class ChromeLabels
{
    public static string Status(IStringLocalizer L, string? status) => status switch
    {
        "InProgress" => L["InProgress"].Value,
        "Done" => L["Done"].Value,
        _ => L["Todo"].Value
    };

    public static string Status(IStringLocalizer L, TaskItemStatus status) => status switch
    {
        TaskItemStatus.InProgress => L["InProgress"].Value,
        TaskItemStatus.Done => L["Done"].Value,
        _ => L["Todo"].Value
    };

    public static string Priority(IStringLocalizer L, TaskPriority priority) => priority switch
    {
        TaskPriority.Critical => L["Critical"].Value,
        TaskPriority.High => L["High"].Value,
        TaskPriority.Low => L["Low"].Value,
        _ => L["Medium"].Value
    };

    public static string Type(IStringLocalizer L, TaskType type) => type switch
    {
        TaskType.Bug => L["Bug"].Value,
        TaskType.Feature => L["Feature"].Value,
        TaskType.Improvement => L["Improvement"].Value,
        _ => L["Task"].Value
    };

    public static string SubStage(IStringLocalizer L, TaskSubStage? stage) =>
        stage is null ? "-" : L[stage.Value.ToString()].Value;

    public static string Decision(IStringLocalizer L, DecisionStatus status) =>
        L[status.ToString()].Value;

    public static string Constraint(IStringLocalizer L, ConstraintStatus status) =>
        L[status.ToString()].Value;

    public static string Section(IStringLocalizer L, string sectionId) => sectionId switch
    {
        "goals" => L["SectionGoals"].Value,
        "non_goals" => L["SectionNonGoals"].Value,
        "security" => L["SectionSecurity"].Value,
        "definition_of_done" => L["SectionDefinitionOfDone"].Value,
        "architecture" => L["SectionArchitecture"].Value,
        "conventions" => L["SectionConventions"].Value,
        _ => sectionId
    };
}
