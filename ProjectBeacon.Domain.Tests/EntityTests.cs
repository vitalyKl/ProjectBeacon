namespace ProjectBeacon.Domain.Tests;

using ProjectBeacon.Domain.Entities.Devices;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

public sealed class EntityTests
{
    [Fact]
    public void Create_User_SetsId()
    {
        var user = User.Create("test", "test@example.com", "hash");

        Assert.NotEqual(Guid.Empty, user.Id);
    }

    [Fact]
    public void Create_Project_SetsId()
    {
        var project = Project.Create("Test Project", null, Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, project.Id);
    }

    [Fact]
    public void Create_TaskItem_SetsId()
    {
        var task = TaskItem.Create("Test Task", Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, task.Id);
    }

    [Fact]
    public void Create_Label_SetsId()
    {
        var label = Label.Create("Bug", "#ff0000", Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, label.Id);
    }

    [Fact]
    public void Multiple_Creates_Produce_DifferentIds()
    {
        var user1 = User.Create("user1", "u1@example.com", "hash");
        var user2 = User.Create("user2", "u2@example.com", "hash");

        Assert.NotEqual(user1.Id, user2.Id);
    }

    [Fact]
    public void Create_RemainingEntities_SetIds()
    {
        var org = Org.Create("Org");
        var projectId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        Assert.NotEqual(Guid.Empty, org.Id);
        Assert.NotEqual(Guid.Empty, OrgMember.Create(org.Id, userId, MemberRole.Owner).Id);
        Assert.NotEqual(Guid.Empty, OrgInvite.Create(org.Id, "a@b.c", MemberRole.Member, userId).Id);
        Assert.NotEqual(Guid.Empty, UserSession.Create(userId, "127.0.0.1").Id);
        Assert.NotEqual(Guid.Empty, Milestone.Create("M", null, projectId, 0).Id);
        Assert.NotEqual(Guid.Empty, Constraint.Create("must", ConstraintKind.Must, projectId).Id);
        Assert.NotEqual(Guid.Empty, Decision.Create("D", "c", "body", projectId, "breaks").Id);
        Assert.NotEqual(Guid.Empty, ContextSection.Create("goals", "Goals", "x", projectId, ContextScopeType.Project).Id);
        Assert.NotEqual(Guid.Empty, ContextRevision.Create("h", "#", "{}", 1, projectId, []).Id);
        Assert.NotEqual(Guid.Empty, ApiToken.Create("t", "hash", "bcn_xx", projectId, ApiTokenCapability.TaskRead, null, null).Id);
        Assert.NotEqual(Guid.Empty, ProjectMember.Create(projectId, userId, MemberRole.Member).Id);
        Assert.NotEqual(Guid.Empty, ProjectInvite.Create(projectId, "a@b.c", MemberRole.Member, userId).Id);
        Assert.NotEqual(Guid.Empty, TaskComment.Create("hi", Guid.NewGuid(), userId).Id);
        Assert.NotEqual(Guid.Empty, TaskDependency.Create(Guid.NewGuid(), Guid.NewGuid()).Id);
        var label = Label.Create("API", "#000", projectId, "ProjectBeacon.API");
        Assert.NotEqual(Guid.Empty, label.Id);
        Assert.NotEqual(Guid.Empty, LabelPath.Create(label.Id, "ProjectBeacon.API.Tests", projectId).Id);
        Assert.NotEqual(Guid.Empty, Report.Create("t", "#", "{}", projectId, "user", userId.ToString()).Id);
        var link = DecisionTask.Create(Guid.NewGuid(), Guid.NewGuid());
        Assert.NotEqual(Guid.Empty, link.DecisionId);
        Assert.NotEqual(Guid.Empty, link.TaskId);
        Assert.NotEqual(Guid.Empty, DaemonDevice.Create("d", userId, "fp", "h", "bcd_xx").Id);
        Assert.NotEqual(Guid.Empty, WorkstationCommand.Create(Guid.NewGuid(), WorkstationCommandKind.Probe).Id);
        Assert.NotEqual(Guid.Empty, ProjectRuntime.Create(projectId, Guid.NewGuid(), "/repo").Id);
    }
}
