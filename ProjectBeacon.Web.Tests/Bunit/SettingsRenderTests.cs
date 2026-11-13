using System.Globalization;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class SettingsRenderTests : BUnitRenderBase
{
    public SettingsRenderTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
    }

    [Fact]
    public void ProjectMcpServers_RendersForProjectOwner()
    {
        var user = DbContext.Users.Single();
        var project = DbContext.Projects.Single();
        var authProvider = Bunit.Services.GetRequiredService<FakeAuthenticationStateProvider>();
        authProvider.TriggerAuthenticationStateChanged(
            "tester",
            Array.Empty<string>(),
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString())
            },
            "TestAuth");

        var cut = Bunit.RenderComponent<Features.Agents.ProjectMcpServers>(parameters =>
            parameters.Add(component => component.ProjectId, project.Id));

        Assert.Contains("beacon-panel", cut.Markup);
    }

    [Fact]
    public void ProjectManage_HidesGitSyncActionsFromProjectMember()
    {
        var user = DbContext.Users.Single();
        var project = DbContext.Projects.Single();
        var ownerMembership = DbContext.ProjectMembers.Single();
        DbContext.ProjectMembers.Remove(ownerMembership);
        DbContext.ProjectMembers.Add(ProjectBeacon.Domain.Entities.Projects.ProjectMember.Create(
            project.Id, user.Id, ProjectBeacon.Domain.Enums.MemberRole.Member));

        var device = DbContext.Set<ProjectBeacon.Domain.Entities.Devices.DaemonDevice>().Single();
        device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        DbContext.ProjectRuntimes.Add(ProjectBeacon.Domain.Entities.Devices.ProjectRuntime.Create(
            project.Id, device.Id, "/workspace/project"));
        DbContext.SaveChanges();

        var authProvider = Bunit.Services.GetRequiredService<FakeAuthenticationStateProvider>();
        authProvider.TriggerAuthenticationStateChanged(
            "tester",
            Array.Empty<string>(),
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString("D")),
                new System.Security.Claims.Claim("isAdmin", "false"),
                new System.Security.Claims.Claim("project_id", project.Id.ToString("D")),
                new System.Security.Claims.Claim("org_id", project.OrgId.ToString("D"))
            },
            "TestAuth");

        var cut = Bunit.RenderComponent<Features.Projects.ProjectManage>();

        Assert.DoesNotContain("Check status", cut.Markup);
        Assert.DoesNotContain("Synchronize", cut.Markup);
        Assert.Contains("Only a project Owner or Admin can synchronize this repository.", cut.Markup);
    }

    [Fact]
    public void ProjectManage_ShowsGitSyncActionsToProjectOwner()
    {
        var user = DbContext.Users.Single();
        var project = DbContext.Projects.Single();
        var device = DbContext.Set<ProjectBeacon.Domain.Entities.Devices.DaemonDevice>().Single();
        device.Heartbeat(probeJson: """{"capabilities":["git-sync-v1"]}""");
        DbContext.ProjectRuntimes.Add(ProjectBeacon.Domain.Entities.Devices.ProjectRuntime.Create(
            project.Id, device.Id, "/workspace/project"));
        DbContext.SaveChanges();

        var authProvider = Bunit.Services.GetRequiredService<FakeAuthenticationStateProvider>();
        authProvider.TriggerAuthenticationStateChanged(
            "tester",
            Array.Empty<string>(),
            new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString("D")),
                new System.Security.Claims.Claim("isAdmin", "false"),
                new System.Security.Claims.Claim("project_id", project.Id.ToString("D")),
                new System.Security.Claims.Claim("org_id", project.OrgId.ToString("D"))
            },
            "TestAuth");

        var cut = Bunit.RenderComponent<Features.Projects.ProjectManage>();

        Assert.Contains("Check status", cut.Markup);
        Assert.Contains("Synchronize", cut.Markup);
    }

    [Fact]
    public void Settings_RendersPage()
    {
        var cut = Bunit.RenderComponent<Features.Settings.Settings>();

        Assert.Contains("<div", cut.Markup);
    }

    [Fact]
    public void Settings_RendersDeviceSection()
    {
        var cut = Bunit.RenderComponent<Features.Settings.Workstations>();

        Assert.Contains("Device", cut.Markup);
    }

    [Fact]
    public void Settings_RendersAccountNotProject()
    {
        var cut = Bunit.RenderComponent<Features.Settings.Settings>();

        Assert.Contains("Account", cut.Markup);
        Assert.Contains("Appearance", cut.Markup);
        Assert.Contains("Dark", cut.Markup);
        Assert.Contains("Light", cut.Markup);
        Assert.Contains("System", cut.Markup);
        Assert.Contains("Current password", cut.Markup);
        Assert.Contains("Change password", cut.Markup);
        Assert.DoesNotContain("ProjectFolder", cut.Markup);
        Assert.DoesNotContain("Members", cut.Markup);
        Assert.DoesNotContain("InviteMember", cut.Markup);
    }
}
