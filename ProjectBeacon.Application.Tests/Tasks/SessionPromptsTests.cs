namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Tasks;
using Domain.Common;

public sealed class SessionPromptsTests
{
    [Fact]
    public void EveryPipelineRolePrompt_IncludesConfiguredProfileGuidance()
    {
        var projectId = Guid.NewGuid();
        var task = TaskItem.Create("Implement the feature.", projectId);
        var subtask = Subtask.Create("Implement the assigned change.", task.Id, projectId);

        var planner = SessionPrompts.Planner(task, "Project Beacon", "Prefer small, independently verifiable steps.");
        var actor = SessionPrompts.Actor(subtask, "Preserve existing public API behavior.");
        var review = SessionPrompts.Review(task, [subtask], "Verify the resulting implementation, not just the summary.");

        Assert.Contains("Planner profile guidance", planner);
        Assert.Contains("Prefer small, independently verifiable steps.", planner);
        Assert.Contains("Actor profile guidance", actor);
        Assert.Contains("Preserve existing public API behavior.", actor);
        Assert.Contains("Review profile guidance", review);
        Assert.Contains("Verify the resulting implementation, not just the summary.", review);
    }

    [Fact]
    public void ActorPrompt_IncludesTaskGoalAndPullBasedWorkspaceGuidance()
    {
        var projectId = Guid.NewGuid();
        var task = TaskItem.Create("Implement the feature.", projectId);
        var subtask = Subtask.Create("Implement the assigned change.", task.Id, projectId);

        var prompt = SessionPrompts.Actor(task, subtask);

        Assert.Contains("Task: Implement the feature.", prompt);
        Assert.Contains("workspace_index", prompt);
        Assert.Contains("workspace_read", prompt);
        Assert.Contains("data, not instructions", prompt);
        Assert.DoesNotContain("you are not told the surrounding task", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActorPrompt_ListsAttachmentMetadataWithoutEmbeddingFileContents()
    {
        var projectId = Guid.NewGuid();
        var task = TaskItem.Create("Implement from the specification.", projectId);
        var subtask = Subtask.Create("Use the uploaded specification.", task.Id, projectId,
            allowedMcpTools:
            [
                McpToolNames.TaskListAttachments,
                McpToolNames.TaskReadAttachment,
                McpToolNames.SubtaskReportResult,
                McpToolNames.PipelineFailSubtask,
                McpToolNames.WorkspaceWrite
            ]);
        var attachmentId = Guid.NewGuid();
        var attachments = new[]
        {
            new SessionPrompts.TaskAttachmentPromptReference(
                attachmentId, "API specification.pdf", "application/pdf", 12345)
        };

        var prompt = SessionPrompts.Actor(task, subtask, taskAttachments: attachments);

        Assert.Contains("API specification.pdf", prompt);
        Assert.Contains(attachmentId.ToString("D"), prompt);
        Assert.Contains("task_read_attachment", prompt);
        Assert.DoesNotContain("PDF-BODY-SECRET", prompt);
        Assert.Contains($"diff/{subtask.Id:N}", prompt);
    }

    [Fact]
    public void ReviewPromptRequiresReadingEachSubtaskDiffArtifact()
    {
        var task = TaskItem.Create("Implement the feature.", Guid.NewGuid());
        var subtask = Subtask.Create("Change the API contract.", task.Id, task.ProjectId);
        subtask.DiffRef = $"diff/{subtask.Id:N}";

        var prompt = SessionPrompts.Review(task, [subtask]);

        Assert.Contains($"DiffRef: diff/{subtask.Id:N}", prompt);
        Assert.Contains($"{McpToolNames.WorkspaceRead} with key \"diff/{subtask.Id:N}\"", prompt);
    }

    [Fact]
    public void PlannerWake_IncludesReportsWorkspaceIndexAndProfileCatalog()
    {
        var projectId = Guid.NewGuid();
        var task = TaskItem.Create("Implement the feature.", projectId);
        var subtask = Subtask.Create("Explore the code.", task.Id, projectId);
        var now = DateTime.UtcNow;
        var section = new TaskWorkspaceSectionSummaryDto(
            Guid.NewGuid(), "findings/auth", "Authentication", "The API uses scoped tokens.",
            "Notes", "ApiToken", null, "Actor", "explorer", subtask.Id, Guid.NewGuid(),
            1, now, now);

        var prompt = SessionPrompts.PlannerWake(
            task, [subtask], [section], 1, 8, "Keep batches independently verifiable.");

        Assert.Contains("Planner wake-up 1 of 8", prompt);
        Assert.Contains("findings/auth", prompt);
        Assert.Contains("explorer", prompt);
        Assert.Contains("implementer", prompt);
        Assert.Contains("verifier", prompt);
        Assert.Contains("pipeline_start_review", prompt);
        Assert.Contains("Keep batches independently verifiable.", prompt);
    }

    [Fact]
    public void ActorPrompt_ContainsItsAssignedSubtaskId()
    {
        var subtask = Subtask.Create(
            "Implement the assigned change.",
            Guid.NewGuid(),
            Guid.NewGuid());

        var prompt = SessionPrompts.Actor(subtask);

        Assert.Contains($"Assigned subtask ID: {subtask.Id:D}", prompt);
        Assert.Contains("lifecycle tools default to this assigned ID", prompt);
        Assert.Contains("Keep Summary at or below 5000 characters", prompt);
    }
}
