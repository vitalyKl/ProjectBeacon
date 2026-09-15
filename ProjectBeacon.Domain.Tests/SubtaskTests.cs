namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

public sealed class SubtaskTests
{
    private static Subtask NewSubtask(
        string instructions = "Implement the handler",
        IReadOnlyList<string>? tools = null,
        IReadOnlyList<string>? paths = null)
    {
        return Subtask.Create(
            instructions,
            taskId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            allowedMcpTools: tools,
            allowedPaths: paths);
    }

    [Fact]
    public void Create_SetsDefaults()
    {
        var taskId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var subtask = Subtask.Create("Do the thing", taskId, projectId);

        Assert.Equal("Do the thing", subtask.Instructions);
        Assert.Equal(taskId, subtask.TaskId);
        Assert.Equal(projectId, subtask.ProjectId);
        Assert.Equal(SubtaskStatus.Pending, subtask.Status);
        Assert.Null(subtask.DiffRef);
        Assert.Null(subtask.Summary);
        Assert.Equal(0, subtask.ReopenCount);
        Assert.Empty(subtask.AllowedMcpTools);
        Assert.Empty(subtask.AllowedPaths);
        Assert.NotNull(subtask.CreatedAt);
        Assert.Null(subtask.UpdatedAt);
    }

    [Fact]
    public void Create_WithScope_StoresJsonBacking()
    {
        var subtask = NewSubtask(
            tools: ["beacon_start_work", "beacon_finish_work"],
            paths: ["src/", "tests/"]);

        Assert.Equal("[\"beacon_start_work\",\"beacon_finish_work\"]", subtask.AllowedMcpToolsJson);
        Assert.Equal("[\"src/\",\"tests/\"]", subtask.AllowedPathsJson);
        Assert.Equal(["beacon_start_work", "beacon_finish_work"], subtask.AllowedMcpTools);
        Assert.Equal(["src/", "tests/"], subtask.AllowedPaths);
    }

    [Fact]
    public void Start_PendingToInProgress()
    {
        var subtask = NewSubtask();

        subtask.Start();

        Assert.Equal(SubtaskStatus.InProgress, subtask.Status);
        Assert.NotNull(subtask.UpdatedAt);
    }

    [Fact]
    public void Start_FromInProgress_Throws()
    {
        var subtask = NewSubtask();
        subtask.Start();

        Assert.Throws<InvalidOperationException>(() => subtask.Start());
    }

    [Fact]
    public void ReportResult_FromInProgress_SetsDoneWithArtifacts()
    {
        var subtask = NewSubtask();
        subtask.Start();

        subtask.ReportResult("refs/heads/pipeline/task1", "All tests green");

        Assert.Equal(SubtaskStatus.Done, subtask.Status);
        Assert.Equal("refs/heads/pipeline/task1", subtask.DiffRef);
        Assert.Equal("All tests green", subtask.Summary);
        Assert.NotNull(subtask.UpdatedAt);
    }

    [Fact]
    public void ReportResult_FromPending_Throws()
    {
        var subtask = NewSubtask();

        Assert.Throws<InvalidOperationException>(() => subtask.ReportResult("ref", "summary"));
        Assert.Equal(SubtaskStatus.Pending, subtask.Status);
    }

    [Fact]
    public void Fail_FromPending_SetsFailedWithReason()
    {
        var subtask = NewSubtask();

        subtask.Fail("backend unavailable");

        Assert.Equal(SubtaskStatus.Failed, subtask.Status);
        Assert.Equal("backend unavailable", subtask.Summary);
        Assert.NotNull(subtask.UpdatedAt);
    }

    [Fact]
    public void Fail_FromInProgress_SetsFailed()
    {
        var subtask = NewSubtask();
        subtask.Start();

        subtask.Fail();

        Assert.Equal(SubtaskStatus.Failed, subtask.Status);
        Assert.Null(subtask.Summary);
    }

    [Fact]
    public void Fail_FromDone_Throws()
    {
        var subtask = NewSubtask();
        subtask.Start();
        subtask.ReportResult("ref", "summary");

        Assert.Throws<InvalidOperationException>(() => subtask.Fail());
        Assert.Equal(SubtaskStatus.Done, subtask.Status);
    }

    [Fact]
    public void Fail_FromFailed_Throws()
    {
        var subtask = NewSubtask();
        subtask.Fail();

        Assert.Throws<InvalidOperationException>(() => subtask.Fail());
    }

    [Fact]
    public void Reopen_FromDone_ReturnsToPendingAndAppendsNote()
    {
        var subtask = NewSubtask();
        subtask.Start();
        subtask.ReportResult("ref", "summary");
        var originalInstructions = subtask.Instructions;

        subtask.Reopen("Add the missing test case");

        Assert.Equal(SubtaskStatus.Pending, subtask.Status);
        Assert.Equal(1, subtask.ReopenCount);
        Assert.StartsWith(originalInstructions, subtask.Instructions);
        Assert.Contains("Add the missing test case", subtask.Instructions);
        Assert.NotNull(subtask.UpdatedAt);
    }

    [Fact]
    public void Reopen_FromFailed_ReturnsToPending()
    {
        var subtask = NewSubtask();
        subtask.Fail();

        subtask.Reopen("retry");

        Assert.Equal(SubtaskStatus.Pending, subtask.Status);
        Assert.Equal(1, subtask.ReopenCount);
    }

    [Fact]
    public void Reopen_FromPending_Throws()
    {
        var subtask = NewSubtask();

        Assert.Throws<InvalidOperationException>(() => subtask.Reopen("note"));
        Assert.Equal(0, subtask.ReopenCount);
    }

    [Fact]
    public void Reopen_EmptyNote_Throws()
    {
        var subtask = NewSubtask();
        subtask.Start();
        subtask.ReportResult("ref", "summary");

        Assert.Throws<ArgumentException>(() => subtask.Reopen("  "));
        Assert.Equal(SubtaskStatus.Done, subtask.Status);
    }
}
