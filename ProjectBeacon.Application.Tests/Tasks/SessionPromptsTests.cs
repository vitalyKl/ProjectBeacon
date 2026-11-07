namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Tasks;

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
