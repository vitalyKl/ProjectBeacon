namespace ProjectBeacon.API.Tests;

using Application.Projects;
using Application.Tasks;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;

[Collection("postgres-serial")]
public sealed class ClaimTaskPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public ClaimTaskPostgresTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task ConcurrentClaim_OnlyOneSucceeds()
    {
        Guid taskId;
        await using (var seed = _postgres.CreateContext())
        {
            var org = Org.Create("Atomic Test Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync();
            var project = Project.Create("Atomic Test Project", null, org.Id);
            seed.Projects.Add(project);
            await seed.SaveChangesAsync();
            var task = TaskItem.Create("Atomic Claim Task", project.Id, TaskPriority.Medium, TaskType.Task);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync();
            taskId = task.Id;
        }

        var results = await Task.WhenAll(ClaimTaskAsync(taskId), ClaimTaskAsync(taskId), ClaimTaskAsync(taskId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(2, results.Count(r => !r.Success));
        Assert.Equal("InProgress", results.First(r => r.Success).Value!.Status);
        foreach (var failure in results.Where(r => !r.Success))
            Assert.Equal("Task not found or already claimed.", failure.Error);
    }

    [Fact]
    public async Task SequentialClaim_FirstSucceeds_SecondFails()
    {
        Guid taskId;
        await using (var seed = _postgres.CreateContext())
        {
            var org = Org.Create("Sequential Test Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync();
            var project = Project.Create("Sequential Test Project", null, org.Id);
            seed.Projects.Add(project);
            await seed.SaveChangesAsync();
            var task = TaskItem.Create("Sequential Claim Task", project.Id, TaskPriority.Low, TaskType.Task);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync();
            taskId = task.Id;
        }

        var result1 = await ClaimTaskAsync(taskId);
        var result2 = await ClaimTaskAsync(taskId);

        Assert.True(result1.Success);
        Assert.Equal("InProgress", result1.Value!.Status);
        Assert.False(result2.Success);
        Assert.Equal("Task not found or already claimed.", result2.Error);
    }

    private async Task<Application.Common.Result<TaskItemDto>> ClaimTaskAsync(Guid taskId)
    {
        using (TenantScope.EnterUnscoped())
        {
            var handler = new ClaimTaskHandler(_postgres.CreateFactory());
            return await handler.HandleAsync(new ClaimTaskCommand(new ClaimTaskRequest(taskId, Guid.NewGuid())));
        }
    }
}