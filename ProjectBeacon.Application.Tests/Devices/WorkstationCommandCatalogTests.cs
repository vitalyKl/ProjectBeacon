namespace ProjectBeacon.Application.Tests.Devices;

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
            WorkstationCommandKind.ApplyOpencode,
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
    public void Catalog_RejectsUnknownCommandKinds()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => WorkstationCommandCatalog.Get((WorkstationCommandKind)999));

        Assert.Contains("no catalog entry", exception.Message);
    }
}
