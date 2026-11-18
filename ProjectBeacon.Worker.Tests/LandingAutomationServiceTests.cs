namespace ProjectBeacon.Worker.Tests;

using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Worker.Services;

public sealed class LandingAutomationServiceTests
{
    [Theory]
    [InlineData(LandingAttemptStatus.Previewed, 0, false)]
    [InlineData(LandingAttemptStatus.Previewed, 1, false)]
    [InlineData(LandingAttemptStatus.Previewed, 2, true)]
    [InlineData(LandingAttemptStatus.ExecutePending, 0, false)]
    [InlineData(LandingAttemptStatus.ExecutePending, 1, false)]
    [InlineData(LandingAttemptStatus.ExecutePending, 2, true)]
    [InlineData(LandingAttemptStatus.Landed, 2, false)]
    [InlineData(LandingAttemptStatus.RevertPending, 2, false)]
    public void ShouldDispatchExecute_RecoversOnlyApprovedAutomationLevels(
        LandingAttemptStatus status,
        int autonomyLevel,
        bool expected)
    {
        Assert.Equal(expected, LandingAutomationService.ShouldDispatchExecute(status, autonomyLevel));
    }

    [Theory]
    [InlineData(LandingAttemptStatus.RevertPending, "Off", null, true)]
    [InlineData(LandingAttemptStatus.RevertPending, "OnPostCheckFail", 1, true)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "OnPostCheckFail", 1, true)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "Off", 1, false)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "OnPostCheckFail", 0, false)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "OnPostCheckFail", -1, false)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "OnPostCheckFail", null, false)]
    public void ShouldDispatchRevert_SeparatesNewAutomaticRevertsFromPendingRecovery(
        LandingAttemptStatus status,
        string mode,
        int? checkExitCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            LandingAutomationService.ShouldDispatchRevert(status, mode, checkExitCode));
    }
}
