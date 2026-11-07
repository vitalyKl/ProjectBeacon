namespace ProjectBeacon.Application.Tests.Devices;

using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Devices;
using ProjectBeacon.Domain.Enums;
using Xunit;

public sealed class WorkstationCommandCatalogTests
{
    [Fact]
    public void Catalog_CoversEveryDefinedCommandKind()
    {
        var kinds = Enum.GetValues<WorkstationCommandKind>();

        Assert.Equal(kinds.Length, WorkstationCommandCatalog.All.Count);
        foreach (var kind in kinds)
            Assert.Equal(kind, WorkstationCommandCatalog.Get(kind).Kind);
    }

    [Fact]
    public void Catalog_RequiresProjectRuntimeForProjectCommands()
    {
        var expected = new[]
        {
            WorkstationCommandKind.InitProject,
            WorkstationCommandKind.ChatEnsureSession,
            WorkstationCommandKind.ChatPrompt,
            WorkstationCommandKind.RunEvalTurn,
            WorkstationCommandKind.RunReviewCheck,
            WorkstationCommandKind.RunPipelineSession
        };

        foreach (var kind in Enum.GetValues<WorkstationCommandKind>())
        {
            var scope = WorkstationCommandCatalog.Get(kind).Scope;
            Assert.Equal(expected.Contains(kind), scope == WorkstationCommandScope.ProjectRuntime);
            Assert.Equal(scope == WorkstationCommandScope.ProjectRuntime, CommandSandbox.IsProjectKind(kind));
        }
    }

    [Fact]
    public void RunPipelineSession_DeclaresRequiredAndOptionalPayloadFields()
    {
        var descriptor = WorkstationCommandCatalog.Get(WorkstationCommandKind.RunPipelineSession);

        Assert.Equal(
            "{projectId:guid,taskId:guid,subtaskId?:guid,sessionId:guid,role:string,title?:string,prompt:string,model:string,contextLimit?:integer,reasoningEffort?:string}",
            descriptor.PayloadContract);
        Assert.Equal(OperationRetrySemantics.AtMostOnce, descriptor.RetrySemantics);
        Assert.Equal(WorkstationCommandScope.ProjectRuntime, descriptor.Scope);
    }

    [Fact]
    public void ClientUpdateCommands_AreDeviceScopedAndUseSafeRetrySemantics()
    {
        var check = WorkstationCommandCatalog.Get(WorkstationCommandKind.CheckClientUpdate);
        var update = WorkstationCommandCatalog.Get(WorkstationCommandKind.UpdateClient);

        Assert.Equal(WorkstationCommandScope.Device, check.Scope);
        Assert.Equal(OperationRetrySemantics.ReadOnly, check.RetrySemantics);
        Assert.Equal(WorkstationCommandScope.Device, update.Scope);
        Assert.Equal(OperationRetrySemantics.AtMostOnce, update.RetrySemantics);
    }


    [Fact]
    public void Catalog_CentralizesDeliveryLaneAndLeaseMetadata()
    {
        var controlLaneKinds = new[]
        {
            WorkstationCommandKind.ChatAbort,
            WorkstationCommandKind.ChatSwitchAgent,
            WorkstationCommandKind.ChatReplyForm
        };
        var controlPollingKinds = new[]
        {
            WorkstationCommandKind.ChatPrompt,
            WorkstationCommandKind.RunPipelineSession
        };

        foreach (var kind in Enum.GetValues<WorkstationCommandKind>())
        {
            var descriptor = WorkstationCommandCatalog.Get(kind);
            Assert.Equal(
                descriptor.RetrySemantics == OperationRetrySemantics.AtMostOnce,
                CommandDelivery.IsAtMostOnce(kind));
            Assert.Equal(
                descriptor.RetrySemantics != OperationRetrySemantics.AtMostOnce,
                CommandDelivery.IsIdempotent(kind));
            Assert.Equal(
                descriptor.ReconcileBacked,
                CommandDelivery.IsReconcileBacked(kind));
            Assert.Equal(descriptor.LeaseDuration, CommandLease.For(kind));
            Assert.Equal(controlLaneKinds.Contains(kind), descriptor.Lane == WorkstationCommandLane.Control);
            Assert.Equal(controlPollingKinds.Contains(kind), descriptor.StartsControlLane);
        }

        Assert.Equal(TimeSpan.FromMinutes(CommandLease.PipelineMinutes),
            WorkstationCommandCatalog.Get(WorkstationCommandKind.RunPipelineSession).LeaseDuration);
    }

    [Fact]
    public void LegacyExternalAgentCommands_AreDisabledAndDeviceScoped()
    {
        foreach (var kind in new[]
        {
            WorkstationCommandKind.RetiredAgentConfigApply,
            WorkstationCommandKind.RetiredProviderSync
        })
        {
            var descriptor = WorkstationCommandCatalog.Get(kind);
            Assert.False(descriptor.Enabled);
            Assert.Equal(WorkstationCommandScope.Device, descriptor.Scope);
            Assert.False(CommandSandbox.IsProjectKind(kind));
        }
    }

    [Fact]
    public void Catalog_RejectsUnknownCommandKinds()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkstationCommandCatalog.Get((WorkstationCommandKind)999));

        Assert.Contains("no catalog entry", exception.Message);
    }
}
