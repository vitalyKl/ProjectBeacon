using MudBlazor;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Web.Theme;

namespace ProjectBeacon.Web.Tests;

public sealed class ChipPaletteTests
{
    [Theory]
    [InlineData("Done", Color.Success)]
    [InlineData("InProgress", Color.Info)]
    [InlineData("Todo", Color.Default)]
    [InlineData("unknown", Color.Default)]
    [InlineData(null, Color.Default)]
    public void ForStatus_MapsSemanticColor(string? status, Color expected)
    {
        Assert.Equal(expected, ChipPalette.ForStatus(status));
    }

    [Fact]
    public void ForPriority_MapsCriticalAndHigh()
    {
        Assert.Equal(Color.Error, ChipPalette.ForPriority(TaskPriority.Critical));
        Assert.Equal(Color.Warning, ChipPalette.ForPriority(TaskPriority.High));
        Assert.Equal(Color.Default, ChipPalette.ForPriority(TaskPriority.Medium));
        Assert.Equal(Color.Default, ChipPalette.ForPriority(TaskPriority.Low));
    }

    [Fact]
    public void ForType_MapsBugToError()
    {
        Assert.Equal(Color.Error, ChipPalette.ForType(TaskType.Bug));
        Assert.Equal(Color.Default, ChipPalette.ForType(TaskType.Task));
        Assert.Equal(Color.Default, ChipPalette.ForType(TaskType.Feature));
        Assert.Equal(Color.Default, ChipPalette.ForType(TaskType.Improvement));
    }

    [Fact]
    public void ForDecisionStatus_MapsSemanticColor()
    {
        Assert.Equal(Color.Info, ChipPalette.ForDecisionStatus(DecisionStatus.Proposed));
        Assert.Equal(Color.Success, ChipPalette.ForDecisionStatus(DecisionStatus.Accepted));
        Assert.Equal(Color.Default, ChipPalette.ForDecisionStatus(DecisionStatus.Superseded));
        Assert.Equal(Color.Warning, ChipPalette.ForDecisionStatus(DecisionStatus.Deprecated));
    }

    [Fact]
    public void ForConstraintStatus_MapsSemanticColor()
    {
        Assert.Equal(Color.Info, ChipPalette.ForConstraintStatus(ConstraintStatus.Proposed));
        Assert.Equal(Color.Success, ChipPalette.ForConstraintStatus(ConstraintStatus.Active));
        Assert.Equal(Color.Error, ChipPalette.ForConstraintStatus(ConstraintStatus.Rejected));
    }
}
