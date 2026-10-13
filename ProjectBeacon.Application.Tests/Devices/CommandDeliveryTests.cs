namespace ProjectBeacon.Application.Tests;

using Application.Devices;
using Domain.Enums;

public sealed class CommandDeliveryTests
{
    // AtMostOnce: re-execution produces a harmful duplicate.
    [Theory]
    [InlineData(WorkstationCommandKind.ChatEnsureSession)]
    [InlineData(WorkstationCommandKind.ChatPrompt)]
    [InlineData(WorkstationCommandKind.RunEvalTurn)]
    [InlineData(WorkstationCommandKind.RunReviewCheck)]
    public void IsAtMostOnce_ExpectedKinds_ReturnsTrue(WorkstationCommandKind kind)
    {
        Assert.True(CommandDelivery.IsAtMostOnce(kind));
    }

    // Idempotent: re-execution converges to the same state.
    [Theory]
    [InlineData(WorkstationCommandKind.Probe)]
    [InlineData(WorkstationCommandKind.ListDir)]
    [InlineData(WorkstationCommandKind.Install)]
    [InlineData(WorkstationCommandKind.InitProject)]
    [InlineData(WorkstationCommandKind.ApplyOpencode)]
    [InlineData(WorkstationCommandKind.ScanGguf)]
    [InlineData(WorkstationCommandKind.ReloadProxy)]
    [InlineData(WorkstationCommandKind.UnloadProxy)]
    [InlineData(WorkstationCommandKind.SwapModel)]
    [InlineData(WorkstationCommandKind.SaveWorkstation)]
    [InlineData(WorkstationCommandKind.ChatAbort)]
    [InlineData(WorkstationCommandKind.ConfigureOpenCode)]
    [InlineData(WorkstationCommandKind.ReconcileDesired)]
    public void IsAtMostOnce_IdempotentKinds_ReturnsFalse(WorkstationCommandKind kind)
    {
        Assert.False(CommandDelivery.IsAtMostOnce(kind));
    }

    [Theory]
    [InlineData(WorkstationCommandKind.Probe)]
    [InlineData(WorkstationCommandKind.ListDir)]
    [InlineData(WorkstationCommandKind.Install)]
    [InlineData(WorkstationCommandKind.InitProject)]
    [InlineData(WorkstationCommandKind.ApplyOpencode)]
    [InlineData(WorkstationCommandKind.ScanGguf)]
    [InlineData(WorkstationCommandKind.ReloadProxy)]
    [InlineData(WorkstationCommandKind.UnloadProxy)]
    [InlineData(WorkstationCommandKind.SwapModel)]
    [InlineData(WorkstationCommandKind.SaveWorkstation)]
    [InlineData(WorkstationCommandKind.ChatAbort)]
    [InlineData(WorkstationCommandKind.ConfigureOpenCode)]
    [InlineData(WorkstationCommandKind.ReconcileDesired)]
    public void IsIdempotent_ExpectedKinds_ReturnsTrue(WorkstationCommandKind kind)
    {
        Assert.True(CommandDelivery.IsIdempotent(kind));
    }

    [Theory]
    [InlineData(WorkstationCommandKind.ChatEnsureSession)]
    [InlineData(WorkstationCommandKind.ChatPrompt)]
    [InlineData(WorkstationCommandKind.RunEvalTurn)]
    [InlineData(WorkstationCommandKind.RunReviewCheck)]
    public void IsIdempotent_AtMostOnceKinds_ReturnsFalse(WorkstationCommandKind kind)
    {
        Assert.False(CommandDelivery.IsIdempotent(kind));
    }

    // ReconcileBacked: a reconciliation loop re-drives the command if it is lost.
    // This does NOT authorize re-execution on retry.
    [Theory]
    [InlineData(WorkstationCommandKind.ApplyOpencode)]
    [InlineData(WorkstationCommandKind.ReloadProxy)]
    [InlineData(WorkstationCommandKind.SaveWorkstation)]
    [InlineData(WorkstationCommandKind.ConfigureOpenCode)]
    [InlineData(WorkstationCommandKind.ReconcileDesired)]
    public void IsReconcileBacked_ExpectedKinds_ReturnsTrue(WorkstationCommandKind kind)
    {
        Assert.True(CommandDelivery.IsReconcileBacked(kind));
    }

    [Theory]
    [InlineData(WorkstationCommandKind.Probe)]
    [InlineData(WorkstationCommandKind.ListDir)]
    [InlineData(WorkstationCommandKind.Install)]
    [InlineData(WorkstationCommandKind.InitProject)]
    [InlineData(WorkstationCommandKind.ScanGguf)]
    [InlineData(WorkstationCommandKind.UnloadProxy)]
    [InlineData(WorkstationCommandKind.SwapModel)]
    [InlineData(WorkstationCommandKind.ChatEnsureSession)]
    [InlineData(WorkstationCommandKind.ChatPrompt)]
    [InlineData(WorkstationCommandKind.ChatAbort)]
    [InlineData(WorkstationCommandKind.RunEvalTurn)]
    [InlineData(WorkstationCommandKind.RunReviewCheck)]
    public void IsReconcileBacked_OtherKinds_ReturnsFalse(WorkstationCommandKind kind)
    {
        Assert.False(CommandDelivery.IsReconcileBacked(kind));
    }

    [Fact]
    public void EveryKind_IsClassified_Exhaustively()
    {
        var all = Enum.GetValues<WorkstationCommandKind>();
        var atMostOnce = all.Where(CommandDelivery.IsAtMostOnce).ToHashSet();
        var idempotent = all.Where(CommandDelivery.IsIdempotent).ToHashSet();
        var reconcile = all.Where(CommandDelivery.IsReconcileBacked).ToHashSet();

        Assert.Equal(17, all.Length);
        Assert.Equal(4, atMostOnce.Count);
        Assert.Equal(13, idempotent.Count);
        Assert.Equal(5, reconcile.Count);

        Assert.Empty(atMostOnce.Intersect(idempotent));
        Assert.Equal(all.Length, atMostOnce.Count + idempotent.Count);

        foreach (var kind in reconcile)
            Assert.True(idempotent.Contains(kind), $"{kind} is reconcile-backed but not idempotent");
    }

    [Fact]
    public void IsIdempotent_IsNegationOfIsAtMostOnce()
    {
        foreach (var kind in Enum.GetValues<WorkstationCommandKind>())
            Assert.Equal(CommandDelivery.IsAtMostOnce(kind), !CommandDelivery.IsIdempotent(kind));
    }
}
