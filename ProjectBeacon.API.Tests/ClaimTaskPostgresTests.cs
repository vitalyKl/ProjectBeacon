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
        Guid taskId, projectId;
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
            projectId = project.Id;
        }

        var results = await Task.WhenAll(ClaimTaskAsync(taskId, projectId), ClaimTaskAsync(taskId, projectId), ClaimTaskAsync(taskId, projectId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(2, results.Count(r => !r.Success));
        Assert.Equal("InProgress", results.First(r => r.Success).Value!.Status);
        foreach (var failure in results.Where(r => !r.Success))
            Assert.Equal("Task not found or already claimed.", failure.Error);
    }

    [Fact]
    public async Task SequentialClaim_FirstSucceeds_SecondFails()
    {
        Guid taskId, projectId;
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
            projectId = project.Id;
        }

        var result1 = await ClaimTaskAsync(taskId, projectId);
        var result2 = await ClaimTaskAsync(taskId, projectId);

        Assert.True(result1.Success);
        Assert.Equal("InProgress", result1.Value!.Status);
        Assert.False(result2.Success);
        Assert.Equal("Task not found or already claimed.", result2.Error);
    }

    [Fact]
    public async Task NonMember_CannotClaimForeignProjectTask()
    {
        Guid projectAId, projectBId, taskId;
        await using (var seed = _postgres.CreateContext())
        {
            var org = Org.Create("Claim Isolation Org", null);
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync();
            var projectA = Project.Create("Claim Isolation Project A", null, org.Id);
            var projectB = Project.Create("Claim Isolation Project B", null, org.Id);
            seed.Projects.Add(projectA);
            seed.Projects.Add(projectB);
            await seed.SaveChangesAsync();
            var userA = User.Create("claim-iso-user-a", "claim-iso-user-a@beacon.local", "hash-a");
            var userB = User.Create("claim-iso-user-b", "claim-iso-user-b@beacon.local", "hash-b");
            seed.Users.Add(userA);
            seed.Users.Add(userB);
            await seed.SaveChangesAsync();
            seed.ProjectMembers.Add(ProjectMember.Create(projectA.Id, userA.Id, MemberRole.Member));
            seed.ProjectMembers.Add(ProjectMember.Create(projectB.Id, userB.Id, MemberRole.Member));
            var task = TaskItem.Create("Claim Isolation Task", projectB.Id, TaskPriority.Medium, TaskType.Task);
            seed.Tasks.Add(task);
            await seed.SaveChangesAsync();
            projectAId = projectA.Id;
            projectBId = projectB.Id;
            taskId = task.Id;
        }

        var foreign = await ClaimTaskAsync(taskId, projectAId);
        Assert.False(foreign.Success);
        Assert.Equal("Task not found or already claimed.", foreign.Error);

        var own = await ClaimTaskAsync(taskId, projectBId);
        Assert.True(own.Success);
        Assert.Equal("InProgress", own.Value!.Status);
    }

    private async Task<Application.Common.Result<TaskItemDto>> ClaimTaskAsync(Guid taskId, Guid projectId)
    {
        using (TenantScope.EnterUnscoped())
        {
            var handler = new ClaimTaskHandler(_postgres.CreateFactory());
            return await handler.HandleAsync(new ClaimTaskCommand(new ClaimTaskRequest(taskId, Guid.NewGuid(), projectId)));
        }
    }
}
