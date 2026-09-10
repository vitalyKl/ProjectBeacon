namespace ProjectBeacon.Web.Tests;

public sealed class UnitTests
{
    [Fact]
    public void Web_Project_Builds()
    {
        var assembly = typeof(ProjectBeacon.Web.Features.Dashboard.Dashboard).Assembly;

        Assert.NotNull(assembly);
        Assert.Equal("ProjectBeacon.Web", assembly.GetName().Name);
    }
}
