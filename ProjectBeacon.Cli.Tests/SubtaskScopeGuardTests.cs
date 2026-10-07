using ProjectBeacon.Cli.Mcp;

namespace ProjectBeacon.Cli.Tests;

public sealed class SubtaskScopeGuardTests
{
    private static readonly Guid SubtaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ── UsesPath: all 9 path tools ──────────────────────────────────────────

    [Theory]
    [InlineData("read_file")]
    [InlineData("write_file")]
    [InlineData("apply_patch")]
    [InlineData("get_tree")]
    [InlineData("search_code")]
    [InlineData("get_changed_scope")]
    [InlineData("get_signatures")]
    [InlineData("get_callers")]
    [InlineData("hash_range")]
    public void UsesPath_True_ForAllNinePathTools(string tool)
    {
        Assert.True(SubtaskScopeGuard.UsesPath(tool));
    }

    [Theory]
    [InlineData("create_task")]
    [InlineData("set_task_status")]
    [InlineData("pipeline_approve")]
    [InlineData("unknown_tool")]
    [InlineData("")]
    public void UsesPath_False_ForNonPathTools(string tool)
    {
        Assert.False(SubtaskScopeGuard.UsesPath(tool));
    }

    // ── Existing tests (preserved) ──────────────────────────────────────────

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
            new SubtaskScopeGuard.Allowlist(OtherId, "InProgress", ["write_file"], [])
        };
        var error = SubtaskScopeGuard.Reject("read_file", null, lists, null);
        Assert.Contains("subtaskId", error);
    }

    // ── ParsePipeline: happy path ───────────────────────────────────────────

    [Fact]
    public void ParsePipeline_ValidJson_ReturnsAllowlists()
    {
        var json = """
        {
            "subtasks": [
                {"id": "11111111-1111-1111-1111-111111111111", "status": "InProgress",
                 "allowedMcpTools": ["read_file", "beacon_write_file"],
                 "allowedPaths": ["src", "docs"]},
                {"id": "22222222-2222-2222-2222-222222222222", "status": "Todo",
                 "allowedMcpTools": [],
                 "allowedPaths": []}
            ]
        }
        """;
        var result = SubtaskScopeGuard.ParsePipeline(json);
        Assert.Equal(2, result.Count);

        var first = result[0];
        Assert.Equal(SubtaskId, first.Id);
        Assert.Equal("InProgress", first.Status);
        Assert.Equal(2, first.Tools.Count);
        Assert.Contains("read_file", first.Tools);
        Assert.Contains("beacon_write_file", first.Tools);
        Assert.Equal(2, first.Paths.Count);
        Assert.Contains("src", first.Paths);
        Assert.Contains("docs", first.Paths);

        var second = result[1];
        Assert.Equal(OtherId, second.Id);
        Assert.Equal("Todo", second.Status);
        Assert.Empty(second.Tools);
        Assert.Empty(second.Paths);
    }

    [Fact]
    public void ParsePipeline_MissingStatusDefaultsToEmpty()
    {
        var json = """
        {"subtasks": [{"id": "11111111-1111-1111-1111-111111111111", "allowedMcpTools": ["read_file"], "allowedPaths": []}]}
        """;
        var result = SubtaskScopeGuard.ParsePipeline(json);
        Assert.Single(result);
        Assert.Equal("", result[0].Status);
    }

    // ── ParsePipeline: edge cases (fail-open) ──────────────────────────────

    [Fact]
    public void ParsePipeline_MalformedJson_ReturnsEmpty()
    {
        Assert.Empty(SubtaskScopeGuard.ParsePipeline("not json at all"));
        Assert.Empty(SubtaskScopeGuard.ParsePipeline("{broken"));
        Assert.Empty(SubtaskScopeGuard.ParsePipeline(""));
    }

    [Fact]
    public void ParsePipeline_MissingSubtasksKey_ReturnsEmpty()
    {
        Assert.Empty(SubtaskScopeGuard.ParsePipeline("{}"));
        Assert.Empty(SubtaskScopeGuard.ParsePipeline("""{"other": "value"}"""));
    }

    [Fact]
    public void ParsePipeline_NonArraySubtasks_ReturnsEmpty()
    {
        Assert.Empty(SubtaskScopeGuard.ParsePipeline("""{"subtasks": "not-an-array"}"""));
        Assert.Empty(SubtaskScopeGuard.ParsePipeline("""{"subtasks": {"id": "x"}}"""));
    }

    [Fact]
    public void ParsePipeline_InvalidGuids_AreSkipped()
    {
        var json = """
        {
            "subtasks": [
                {"id": "not-a-guid", "status": "InProgress", "allowedMcpTools": ["read_file"], "allowedPaths": []},
                {"id": "11111111-1111-1111-1111-111111111111", "status": "InProgress", "allowedMcpTools": [], "allowedPaths": []}
            ]
        }
        """;
        var result = SubtaskScopeGuard.ParsePipeline(json);
        Assert.Single(result);
        Assert.Equal(SubtaskId, result[0].Id);
    }

    [Fact]
    public void ParsePipeline_MissingIdIsSkipped()
    {
        var json = """
        {
            "subtasks": [
                {"status": "InProgress", "allowedMcpTools": ["read_file"], "allowedPaths": []},
                {"id": "11111111-1111-1111-1111-111111111111", "status": "InProgress", "allowedMcpTools": [], "allowedPaths": []}
            ]
        }
        """;
        var result = SubtaskScopeGuard.ParsePipeline(json);
        Assert.Single(result);
    }

    [Fact]
    public void ParsePipeline_NonArrayAllowedTools_ReturnsEmptyList()
    {
        var json = """
        {"subtasks": [{"id": "11111111-1111-1111-1111-111111111111", "status": "InProgress",
         "allowedMcpTools": "read_file", "allowedPaths": "src"}]}
        """;
        var result = SubtaskScopeGuard.ParsePipeline(json);
        Assert.Single(result);
        Assert.Empty(result[0].Tools);
        Assert.Empty(result[0].Paths);
    }

    [Fact]
    public void ParsePipeline_NonStringEntriesInAllowedTools_AreDropped()
    {
        var json = """
        {"subtasks": [{"id": "11111111-1111-1111-1111-111111111111", "status": "InProgress",
         "allowedMcpTools": ["read_file", 42, null, ""], "allowedPaths": []}]}
        """;
        var result = SubtaskScopeGuard.ParsePipeline(json);
        Assert.Single(result);
        Assert.Equal(["read_file"], result[0].Tools);
    }

    // ── Reject: explicit subtaskId ─────────────────────────────────────────

    [Fact]
    public void ExplicitSubtaskId_Matches_ReturnsThatAllowlist()
    {
        var lists = new[]
        {
            new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", ["read_file"], []),
            new SubtaskScopeGuard.Allowlist(OtherId, "InProgress", ["write_file"], [])
        };
        // Without explicit ID → error (multiple restricted)
        Assert.NotNull(SubtaskScopeGuard.Reject("read_file", null, lists, null));

        // With explicit ID → resolves to the matching allowlist
        var ok = SubtaskScopeGuard.Reject("read_file", null, lists, SubtaskId);
        Assert.Null(ok);

        var rejected = SubtaskScopeGuard.Reject("write_file", null, lists, SubtaskId);
        Assert.Contains("not allowed", rejected);
    }

    [Fact]
    public void ExplicitSubtaskId_NotFound_ReturnsError()
    {
        var missing = Guid.NewGuid();
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", ["read_file"], []) };
        var error = SubtaskScopeGuard.Reject("read_file", null, lists, missing);
        Assert.Contains("not found", error);
    }

    // ── Reject: requestedPaths variations ──────────────────────────────────

    [Fact]
    public void NullRequestedPaths_WithPathLimits_ReturnsNull_NoRestriction()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", [], ["src"]) };
        // null requestedPaths means the tool didn't provide paths → no path check
        Assert.Null(SubtaskScopeGuard.Reject("read_file", null, lists, null));
    }

    [Fact]
    public void EmptyRequestedPaths_WithPathLimits_IsRejected()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", [], ["src"]) };
        var error = SubtaskScopeGuard.Reject("get_tree", [], lists, null);
        Assert.Contains("outside", error);
    }

    [Fact]
    public void PathWithParentSegment_IsRejected()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", [], ["src"]) };
        var error = SubtaskScopeGuard.Reject("read_file", ["src/../secret"], lists, null);
        Assert.Contains("outside", error);
    }

    [Fact]
    public void PathPrefix_MatchIsExactOrSubdirectory()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", [], ["src"]) };

        // Exact match
        Assert.Null(SubtaskScopeGuard.Reject("read_file", ["src"], lists, null));

        // Subdirectory
        Assert.Null(SubtaskScopeGuard.Reject("read_file", ["src/components/widget.cs"], lists, null));

        // Sibling prefix should NOT match (e.g. "src2" is not under "src")
        var error = SubtaskScopeGuard.Reject("read_file", ["src2/other.cs"], lists, null);
        Assert.Contains("outside", error);
    }

    [Fact]
    public void PathMatch_IsCaseInsensitive()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", [], ["src"]) };
        Assert.Null(SubtaskScopeGuard.Reject("read_file", ["SRC/FILE.cs"], lists, null));
    }

    // ── Reject: tool allowlist ─────────────────────────────────────────────

    [Fact]
    public void ToolMatch_IsCaseInsensitive()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", ["READ_FILE"], []) };
        Assert.Null(SubtaskScopeGuard.Reject("read_file", null, lists, null));
    }

    [Fact]
    public void NonPathTool_IgnoresRequestedPaths()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", ["create_task"], []) };
        // Non-path tool with paths in allowlist → paths are irrelevant
        Assert.Null(SubtaskScopeGuard.Reject("create_task", ["anything"], lists, null));
    }

    // ── Reject: status filtering ───────────────────────────────────────────

    [Fact]
    public void TodoStatus_IsNotInProgress_NotSelected()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "Todo", ["read_file"], []) };
        // Todo subtask is not InProgress → not selected → no restriction
        Assert.Null(SubtaskScopeGuard.Reject("write_file", null, lists, null));
    }

    [Fact]
    public void DoneStatus_IsNotInProgress_NotSelected()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "Done", ["read_file"], []) };
        Assert.Null(SubtaskScopeGuard.Reject("write_file", null, lists, null));
    }

    [Fact]
    public void NoInProgressSubtasks_ReturnsNull()
    {
        var lists = new[]
        {
            new SubtaskScopeGuard.Allowlist(SubtaskId, "Todo", ["read_file"], []),
            new SubtaskScopeGuard.Allowlist(OtherId, "Done", ["write_file"], [])
        };
        Assert.Null(SubtaskScopeGuard.Reject("write_file", null, lists, null));
    }

    [Fact]
    public void EmptySubtaskList_ReturnsNull()
    {
        Assert.Null(SubtaskScopeGuard.Reject("write_file", ["src/a.cs"], [], null));
    }

    // ── Reject: explicit ID with empty allowlist ──────────────────────────

    [Fact]
    public void ExplicitSubtaskId_WithEmptyToolsAndPaths_ReturnsNull()
    {
        var lists = new[] { new SubtaskScopeGuard.Allowlist(SubtaskId, "InProgress", [], []) };
        // Explicit ID matches but has no restrictions → null
        Assert.Null(SubtaskScopeGuard.Reject("write_file", ["src/a.cs"], lists, SubtaskId));
    }

    // ── Fail-open: document that missing/invalid pipeline data means no restriction ──

    [Fact]
    public void FailOpen_MalformedPipelineJson_MeansNoRestriction()
    {
        // ParsePipeline returns [] on malformed JSON.
        // Reject with [] and null explicitSubtaskId → Resolve returns null → no restriction.
        var lists = SubtaskScopeGuard.ParsePipeline("garbage");
        Assert.Null(SubtaskScopeGuard.Reject("write_file", ["src/a.cs"], lists, null));
    }

    [Fact]
    public void FailOpen_EmptySubtasksArray_MeansNoRestriction()
    {
        var lists = SubtaskScopeGuard.ParsePipeline("""{"subtasks": []}""");
        Assert.Null(SubtaskScopeGuard.Reject("write_file", ["src/a.cs"], lists, null));
    }

    [Fact]
    public void FailOpen_InProgressButNoLimits_MeansNoRestriction()
    {
        // InProgress subtask with empty tools AND empty paths → not "restricted"
        var lists = SubtaskScopeGuard.ParsePipeline(
            """{"subtasks": [{"id": "11111111-1111-1111-1111-111111111111", "status": "InProgress", "allowedMcpTools": [], "allowedPaths": []}]}""");
        Assert.Null(SubtaskScopeGuard.Reject("write_file", ["src/a.cs"], lists, null));
    }
}
