using ProjectBeacon.Application;
using ProjectBeacon.Domain.Entities.Devices;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Mail;
using ProjectBeacon.Web;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using System.Security.Claims;

namespace ProjectBeacon.Web.Tests.Bunit;

public abstract class BUnitRenderBase : IDisposable
{
    private readonly SqliteConnection _sqliteConnection;
    private readonly DbContextOptions<BeaconDbContext> _options;
    private TestContext _bunit;

    protected TestContext Bunit => _bunit;
    protected BeaconDbContext DbContext => _dbContext;

    private readonly BeaconDbContext _dbContext;

    protected BUnitRenderBase()
    {
        _sqliteConnection = new SqliteConnection("Data Source=:memory:");
        _sqliteConnection.Open();

        _options = new DbContextOptionsBuilder<BeaconDbContext>()
          .UseSqlite(_sqliteConnection)
          .Options;

        var tenant = new TenantContext();
        tenant.Assign(null, null, true);

        _dbContext = new BeaconDbContext(_options, tenant);
        _dbContext.Database.EnsureCreated();

        using var scope = TenantScope.EnterUnscoped();
        SeedData(_dbContext);

        _bunit = new TestContext();
        _bunit.JSInterop.Mode = JSRuntimeMode.Loose;
        _bunit.Services.AddOptions();
        _bunit.Services.AddDbContext<BeaconDbContext>(o => o.UseSqlite(_sqliteConnection));
        _bunit.Services.AddScoped<IDbContextFactory<BeaconDbContext>, BeaconDbFactory>(_ => new BeaconDbFactory(_options, tenant));
        _bunit.Services.AddApplicationHandlers();
        _bunit.Services.AddMudServices();
        _bunit.Services.AddLocalization();
        _bunit.Services.AddSingleton(tenant);
        _bunit.Services.AddScoped(_ => TenantScope.EnterUnscoped());

        var authProvider = new FakeAuthenticationStateProvider();
        _bunit.Services.AddSingleton<AuthenticationStateProvider>(authProvider);
        _bunit.Services.AddSingleton(authProvider);

        _bunit.Services.AddSingleton<IEmailSender, FakeEmailSender>();
        _bunit.Services.AddLogging();

        authProvider.TriggerAuthenticationStateChanged(
          "tester",
          Array.Empty<string>(),
          new[]
          {
        new Claim(ClaimTypes.NameIdentifier, "user-id"),
        new Claim("isAdmin", "true"),
        new Claim("project_id", "project-id")
          },
          "TestAuth");
    }

    private static void SeedData(BeaconDbContext context)
    {
        var org = Org.Create("Acme");
        context.Orgs.Add(org);

        var user = User.Create("tester", "t@example.com", "pw_hashed");
        context.Users.Add(user);

        var project = Project.Create("TestProject", null, org.Id);
        context.Projects.Add(project);

        context.ProjectMembers.Add(ProjectMember.Create(project.Id, user.Id, MemberRole.Owner));

        context.Labels.Add(Label.Create("UX", "#405189", project.Id));
        context.Labels.Add(Label.Create("API", "#326800", project.Id, "src/api"));

        var task1 = TaskItem.Create("Task One", project.Id);
        context.Tasks.Add(task1);

        var task2 = TaskItem.Create("Task Two", project.Id);
        task2.TransitionTo(TaskItemStatus.InProgress);
        context.Tasks.Add(task2);

        var task3 = TaskItem.Create("Task Three", project.Id);
        task3.SetReviewNotes("Reviewed and approved.");
        task3.TransitionTo(TaskItemStatus.Done);
        context.Tasks.Add(task3);

        var device = DaemonDevice.Create("test-device-1", user.Id, "fp123", "hash1", "bcd_test");
        context.Set<DaemonDevice>().Add(device);

        context.SaveChanges();
    }

    public void Dispose()
    {
        _sqliteConnection.Dispose();
        _bunit.Dispose();
    }
}

public sealed class FakeEmailSender : IEmailSender
{
    public bool IsConfigured => true;
    public System.Threading.Tasks.Task SendAsync(string to, string subject, string textBody, System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
}
