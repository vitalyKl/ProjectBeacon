namespace ProjectBeacon.Application.Tests;

using Application.Common;

public sealed class PathMatcherTests
{
    [Theory]
    [InlineData("apps/api/src/app.ts", "apps/api", true)]
    [InlineData("apps/api", "apps/api", true)]
    [InlineData("apps/api-legacy/src/app.ts", "apps/api", false)]
    [InlineData("apps/api_legacy/foo", "apps/api", false)]
    [InlineData(@"apps\api\foo.ts", "apps/api", true)]
    [InlineData("ProjectBeacon.API/Program.cs", "ProjectBeacon.API", true)]
    [InlineData("ProjectBeacon.API.Tests/x.cs", "ProjectBeacon.API", false)]
    public void MatchesPrefix_UsesPathBoundaries(string path, string prefix, bool expected)
    {
        Assert.Equal(expected, PathMatcher.MatchesPrefix(path, prefix));
    }
}
