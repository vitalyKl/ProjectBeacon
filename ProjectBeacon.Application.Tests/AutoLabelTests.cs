namespace ProjectBeacon.Application.Tests;

using Application.Common;
using Domain.Entities.Projects;

public sealed class AutoLabelTests
{
    [Fact]
    public void Match_PrefersLongerPrefix_AndRespectsBoundaries()
    {
        var projectId = Guid.NewGuid();
        var api = Label.Create("API", "#000", projectId, "ProjectBeacon.API");
        var apiTests = Label.Create("API tests", "#000", projectId, "ProjectBeacon.API.Tests");
        var labels = new[] { api, apiTests };

        Assert.Equal(api.Id, AutoLabel.Match(labels, "ProjectBeacon.API/Program.cs")!.Id);
        Assert.Equal(apiTests.Id, AutoLabel.Match(labels, "ProjectBeacon.API.Tests/Foo.cs")!.Id);
        Assert.Null(AutoLabel.Match(labels, "ProjectBeacon.API.Legacy/x.cs"));
    }

    [Fact]
    public void Match_UsesLabelPathCollection()
    {
        var projectId = Guid.NewGuid();
        var web = Label.Create("Web", "#000", projectId, "ProjectBeacon.Web");
        web.AddPath("ProjectBeacon.Web.Tests");
        var labels = new[] { web };

        Assert.Equal(web.Id, AutoLabel.Match(labels, "ProjectBeacon.Web/Features/Board/Board.razor")!.Id);
        Assert.Equal(web.Id, AutoLabel.Match(labels, "ProjectBeacon.Web.Tests/UnitTests.cs")!.Id);
        Assert.Null(AutoLabel.Match(labels, "ProjectBeacon.Web.Legacy/x.cs"));
    }
}
