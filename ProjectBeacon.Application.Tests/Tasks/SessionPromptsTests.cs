namespace ProjectBeacon.Application.Tests;

using Application.Tasks;
using Domain.Entities.Tasks;

public sealed class SessionPromptsTests
{
    [Fact]
    public void ActorPrompt_ContainsItsAssignedSubtaskId()
    {
        var subtask = Subtask.Create(
            "Implement the assigned change.",
            Guid.NewGuid(),
            Guid.NewGuid());

        var prompt = SessionPrompts.Actor(subtask);

        Assert.Contains($"Assigned subtask ID: {subtask.Id:D}", prompt);
        Assert.Contains("defaults to this assigned ID when subtaskId is omitted", prompt);
    }
}
