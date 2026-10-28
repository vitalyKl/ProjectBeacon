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
            WorkstationCommandKind.RunReviewCheck
        };

        foreach (var kind in Enum.GetValues<WorkstationCommandKind>())
        {
            var scope = WorkstationCommandCatalog.Get(kind).Scope;
            Assert.Equal(expected.Contains(kind), scope == WorkstationCommandScope.ProjectRuntime);
            Assert.Equal(scope == WorkstationCommandScope.ProjectRuntime, CommandSandbox.IsProjectKind(kind));
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
