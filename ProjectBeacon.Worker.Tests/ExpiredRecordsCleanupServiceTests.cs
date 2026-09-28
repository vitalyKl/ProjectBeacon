namespace ProjectBeacon.Worker.Tests;

using Domain.Common;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectBeacon.Worker.Services;

public class ExpiredRecordsCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;

    public ExpiredRecordsCleanupServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(_connection).Options;
        _db = new BeaconDbContext(options);
        _unscoped = TenantScope.EnterUnscoped();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _unscoped.Dispose();
    }

    private static ExpiredRecordsCleanupService CreateService(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<BeaconDbContext>().UseSqlite(connection).Options;
        var factory = new BeaconDbFactory(options, null);
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<BeaconDbContext>>(factory);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return new ExpiredRecordsCleanupService(scopeFactory, NullLogger<ExpiredRecordsCleanupService>.Instance);
    }

    [Fact]
    public async Task NoExpiredRecords_ReturnsZero()
    {
        var service = CreateService(_connection);
        var result = await service.RunCleanupAsync();
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ExpiredUserSession_Deactivated()
    {
        var user = User.Create("user1", "user1@test.com", "hash");
        _db.Users.Add(user);
        var session = UserSession.Create(user.Id, "127.0.0.1", null, TimeSpan.FromHours(-1));
        _db.Sessions.Add(session);
        _db.SaveChanges();

        var service = CreateService(_connection);
        var result = await service.RunCleanupAsync();

        Assert.True(result >= 1);
        _db.ChangeTracker.Clear();
        var reloaded = await _db.Sessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.False(reloaded.IsActive);
    }

    [Fact]
    public async Task ActiveUserSession_Untouched()
    {
        var user = User.Create("user2", "user2@test.com", "hash");
        _db.Users.Add(user);
        var session = UserSession.Create(user.Id, "127.0.0.1");
        _db.Sessions.Add(session);
        _db.SaveChanges();

        var service = CreateService(_connection);
        await service.RunCleanupAsync();

        _db.ChangeTracker.Clear();
        var reloaded = await _db.Sessions.AsNoTracking().FirstAsync(s => s.Id == session.Id);
        Assert.True(reloaded.IsActive);
    }

    [Fact]
    public async Task ExpiredApiToken_Deleted()
    {
        var org = Org.Create("Test Org");
        _db.Orgs.Add(org);
        var project = Project.Create("Test Project", null, org.Id);
        _db.Projects.Add(project);
        var token = ApiToken.Create("test", "hash123", "abc", project.Id, ApiTokenCapability.TaskRead, DateTime.UtcNow.AddHours(-1), null);
        _db.ApiTokens.Add(token);
        _db.SaveChanges();

        var service = CreateService(_connection);
        var result = await service.RunCleanupAsync();

        Assert.True(result >= 1);
        _db.ChangeTracker.Clear();
        var remaining = await _db.ApiTokens.AsNoTracking().AnyAsync(t => t.Id == token.Id);
        Assert.False(remaining);
    }

    [Fact]
    public async Task ExpiredPasswordResetToken_Deleted()
    {
        var user = User.Create("user3", "user3@test.com", "hash");
        _db.Users.Add(user);
        var token = PasswordResetToken.Create(user.Id, "hash123", TimeSpan.FromHours(-1));
        _db.PasswordResetTokens.Add(token);
        _db.SaveChanges();

        var service = CreateService(_connection);
        var result = await service.RunCleanupAsync();

        Assert.True(result >= 1);
        _db.ChangeTracker.Clear();
        var remaining = await _db.PasswordResetTokens.AsNoTracking().AnyAsync(t => t.Id == token.Id);
        Assert.False(remaining);
    }

    [Fact]
    public async Task ExpiredOrgInvite_StatusSetToExpired()
    {
        var org = Org.Create("Test Org");
        _db.Orgs.Add(org);
        var user = User.Create("user4", "user4@test.com", "hash");
        _db.Users.Add(user);
        var invite = OrgInvite.Create(org.Id, "invitee@test.com", MemberRole.Member, user.Id, "hash123");
        _db.OrgInvites.Add(invite);
        _db.SaveChanges();

        await _db.OrgInvites
            .Where(i => i.Id == invite.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiredAt, DateTime.UtcNow.AddHours(-1)));

        var service = CreateService(_connection);
        var result = await service.RunCleanupAsync();

        Assert.True(result >= 1);
        _db.ChangeTracker.Clear();
        var reloaded = await _db.OrgInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.Equal(InviteStatus.Expired, reloaded.Status);
    }

    [Fact]
    public async Task PendingOrgInvite_NotYetExpired_Untouched()
    {
        var org = Org.Create("Test Org");
        _db.Orgs.Add(org);
        var user = User.Create("user5", "user5@test.com", "hash");
        _db.Users.Add(user);
        var invite = OrgInvite.Create(org.Id, "invitee@test.com", MemberRole.Member, user.Id, "hash123");
        _db.OrgInvites.Add(invite);
        _db.SaveChanges();

        var service = CreateService(_connection);
        await service.RunCleanupAsync();

        _db.ChangeTracker.Clear();
        var reloaded = await _db.OrgInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.Equal(InviteStatus.Pending, reloaded.Status);
    }

    [Fact]
    public async Task ExpiredProjectInvite_StatusSetToExpired()
    {
        var org = Org.Create("Test Org");
        _db.Orgs.Add(org);
        var project = Project.Create("Test Project", null, org.Id);
        _db.Projects.Add(project);
        var user = User.Create("user6", "user6@test.com", "hash");
        _db.Users.Add(user);
        var invite = ProjectInvite.Create(project.Id, "invitee@test.com", MemberRole.Member, user.Id, "hash123");
        _db.ProjectInvites.Add(invite);
        _db.SaveChanges();

        await _db.ProjectInvites
            .Where(i => i.Id == invite.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiredAt, DateTime.UtcNow.AddHours(-1)));

        var service = CreateService(_connection);
        var result = await service.RunCleanupAsync();

        Assert.True(result >= 1);
        _db.ChangeTracker.Clear();
        var reloaded = await _db.ProjectInvites.AsNoTracking().FirstAsync(i => i.Id == invite.Id);
        Assert.Equal(InviteStatus.Expired, reloaded.Status);
    }
}
