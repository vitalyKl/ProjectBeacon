namespace ProjectBeacon.Web.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

public sealed class ProjectSwitchHttpTests
{
    [Fact]
    public async Task SwitchProject_ReissuesCookieAndRedirectsToReturnUrl()
    {
        var (factory, client, projectId) = await SetupAsync();
        using var _ = factory;

        var response = await client.GetAsync($"/project/switch?projectId={projectId}&returnUrl=/board", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/board", response.Headers.Location?.ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("BeaconAuth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchProject_RejectsUnknownProject()
    {
        var (factory, client, _) = await SetupAsync();
        using var _ = factory;

        var response = await client.GetAsync($"/project/switch?projectId={Guid.NewGuid()}&returnUrl=/board", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task SwitchProject_RejectsOpenRedirectReturnUrl()
    {
        var (factory, client, projectId) = await SetupAsync();
        using var _ = factory;

        var response = await client.GetAsync($"/project/switch?projectId={projectId}&returnUrl=https%3A%2F%2Fevil.example", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task SwitchProject_Anonymous_ChallengesToLogin()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/project/switch?projectId={Guid.NewGuid()}", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(WebTestFactory Factory, HttpClient Client, Guid ProjectId)> SetupAsync()
    {
        var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var password = await WebTestFactory.GetBootstrapPasswordAsync(client);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = "admin",
            ["password"] = password!
        });
        var login = await client.PostAsync("/v1/auth/cookie-login", form);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        Guid projectId;
        using (TenantScope.EnterUnscoped())
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            var admin = await db.Users.SingleAsync(
                user => user.Login == "admin", TestContext.Current.CancellationToken);
            var org = Org.Create("Org");
            var project = Project.Create("Alpha", null, org.Id);
            db.Orgs.Add(org);
            db.Projects.Add(project);
            db.OrgMembers.Add(OrgMember.Create(org.Id, admin.Id, MemberRole.Owner));
            db.ProjectMembers.Add(ProjectMember.Create(project.Id, admin.Id, MemberRole.Owner));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            projectId = project.Id;
        }

        return (factory, client, projectId);
    }
}
