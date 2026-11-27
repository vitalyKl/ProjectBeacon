namespace ProjectBeacon.Web.Tests.Bunit;

using RoadmapPage = ProjectBeacon.Web.Features.Roadmap.Roadmap;

public sealed class RoadmapEmptyStateTests
{
    [Fact]
    public void EmptyState_IsHiddenWhenReleaseListFailedBeforeAnyReleaseWasLoaded()
    {
        var showEmptyState = RoadmapPage.ShouldShowEmptyState(
            milestoneCount: 0,
            deletedMilestoneCount: 0,
            showCreateForm: false,
            releaseManagementEnabled: true,
            releaseCount: 0,
            showCreateReleaseForm: false,
            hasRoadmapLoadError: false,
            hasReleaseLoadError: true);

        Assert.False(showEmptyState);
    }

    [Fact]
    public void EmptyState_IsShownWhenReleaseListSuccessfullyLoadsEmpty()
    {
        var showEmptyState = RoadmapPage.ShouldShowEmptyState(
            milestoneCount: 0,
            deletedMilestoneCount: 0,
            showCreateForm: false,
            releaseManagementEnabled: true,
            releaseCount: 0,
            showCreateReleaseForm: false,
            hasRoadmapLoadError: false,
            hasReleaseLoadError: false);

        Assert.True(showEmptyState);
    }

    [Fact]
    public void EmptyState_IsNotShownWhenAnyRoadmapSectionHasLoadError()
    {
        var showEmptyState = RoadmapPage.ShouldShowEmptyState(
            milestoneCount: 0,
            deletedMilestoneCount: 0,
            showCreateForm: false,
            releaseManagementEnabled: false,
            releaseCount: 0,
            showCreateReleaseForm: false,
            hasRoadmapLoadError: true,
            hasReleaseLoadError: false);

        Assert.False(showEmptyState);
    }
}
