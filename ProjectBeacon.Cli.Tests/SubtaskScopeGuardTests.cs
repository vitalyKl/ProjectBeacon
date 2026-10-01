using ProjectBeacon.Cli.Mcp;

namespace ProjectBeacon.Cli.Tests;

public sealed class SubtaskScopeGuardTests
{
    private static readonly Guid SubtaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void EmptyAllowlist_DoesNotRestrict()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", [], []) };
        Assert.Null(SubtaskScopeGuard.Reject("write_file", ["src/a.cs"], lists, null));
    }

    [Fact]
    public void ToolOutsideAllowlist_IsRejected()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", ["read_file"], []) };
        var error = SubtaskScopeGuard.Reject("write_file", ["src/a.cs"], lists, null);
        Assert.Contains("not allowed", error);
    }

    [Fact]
    public void BeaconPrefix_MatchesToolName()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", ["beacon_read_file"], ["src"]) };
        Assert.Null(SubtaskScopeGuard.Reject("read_file", ["src/a.cs"], lists, null));
    }

    [Fact]
    public void PathOutsideAllowlist_IsRejected()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", [], ["src"]) };
        var error = SubtaskScopeGuard.Reject("read_file", ["docs/a.md"], lists, null);
        Assert.Contains("outside", error);
    }

    [Fact]
    public void UnscopedPathTool_IsRejectedWhenPathsAreLimited()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", [], ["src"]) };
        var error = SubtaskScopeGuard.Reject("get_tree", [], lists, null);
        Assert.Contains("outside", error);
    }

    [Fact]
    public void MultipleRestrictedSubtasks_RequireAnId()
    {
        var lists = new[]
        {
            new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", ["read_file"], []),
            new SubtaskScopeGuard.Allowlist(Guid.Parse("22222222-2222-2222-2222-222222222222"), "InProgress", ["write_file"], [])
        };
        var error = SubtaskScopeGuard.Reject("read_file", null, lists, null);
        Assert.Contains("subtaskId", error);
    }
}
