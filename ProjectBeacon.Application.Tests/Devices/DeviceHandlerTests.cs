namespace ProjectBeacon.Application.Tests;

using System.Text.Json;
using Application.Common;
using Application.Devices;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed partial class DeviceHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public DeviceHandlerTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private BeaconDbFactory Factory() => HandlerSqlite.Factory(_connection);

    private async Task<User> SeedUserAsync(string login = "dev")
    {
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create(login, login + "@beacon.local", "hash");
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return user;
        }
    }

    private async Task<(User User, Project Project)> SeedMemberAsync()
    {
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("dev", "dev@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(user);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));
            await _db.SaveChangesAsync();
            return (user, project);
        }
    }

    private async Task<(User Owner, User Member, Project Project)> SeedOwnerAndMemberAsync()
    {
        using (TenantScope.EnterUnscoped())
        {
            var owner = User.Create("owner", "owner@beacon.local", "hash");
            var member = User.Create("member", "member@beacon.local", "hash");
            var org = Org.Create("Org");
            _db.Users.Add(owner);
            _db.Users.Add(member);
            _db.Orgs.Add(org);
            await _db.SaveChangesAsync();
            var project = Project.Create("P", null, org.Id);
            _db.Projects.Add(project);
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, owner.Id, MemberRole.Owner));
            _db.ProjectMembers.Add(ProjectMember.Create(project.Id, member.Id, MemberRole.Member));
            await _db.SaveChangesAsync();
            return (owner, member, project);
        }
    }




    private async Task<(User User, Project Project, Guid DeviceId)> OnlineProjectDeviceAsync(string workstationJson = "{}")
    {
        var (user, project) = await SeedMemberAsync();
        var created = await new CreateDeviceHandler(Factory()).HandleAsync(
            new CreateDeviceCommand(new CreateDeviceRequest("laptop", "fp-" + Guid.NewGuid().ToString("N"), user.Id)));
        var deviceId = created.Value!.Id;
        await new HeartbeatDeviceHandler(Factory()).HandleAsync(
            new HeartbeatDeviceCommand(new HeartbeatDeviceRequest(deviceId, "{}", workstationJson)));
        return (user, project, deviceId);
    }
}


