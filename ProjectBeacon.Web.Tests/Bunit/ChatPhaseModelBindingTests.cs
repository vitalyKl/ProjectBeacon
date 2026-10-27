namespace ProjectBeacon.Web.Tests.Bunit;

using System.Security.Claims;
using global::Bunit;
using global::Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Application.Persistence;
using Domain.Entities.Agents;
using Domain.Entities.Chat;
using Domain.Enums;
using ProjectBeacon.Web.Features.Chat;
using ProjectBeacon.Web.Resources;

/// <summary>
/// Quoted values on string component parameters bind as string literals, not expressions.
/// Chat and ChatDock must pass their phase and model-status values with @ expression
/// bindings: without them the session head shows a chip labeled "PhaseLabel" and the model
/// bar shows the literal "ModelStatusLabel" instead of the localized state.
/// </summary>
public sealed class ChatPhaseModelBindingTests : BUnitRenderBase
{
    [Fact]
    public async Task ChatPage_SessionHead_ShowsLocalizedPhaseLabel()
    {
        await SeedSessionAsync("Session One", "alpha-part");
        AuthAsSeededUser();
        var cut = Bunit.RenderComponent<Chat>();
        var head = cut.WaitForElement(".beacon-chat-session-head", TimeSpan.FromSeconds(10));
        var completed = Bunit.Services.GetRequiredService<IStringLocalizer<Web>>()["ChatCompleted"].Value;
        Assert.Contains(completed, head.TextContent);
        Assert.DoesNotContain("PhaseLabel", head.TextContent);
    }

    [Fact]
    public async Task ChatPage_ModelBar_ShowsRealModelStatus()
    {
        var user = DbContext.Users.Single(u => u.Login == "tester");
        var backend = LocalModelBackend.Create("test-model", ModelBackendType.LlamaCpp, "llama.exe serve", 4096, 0, user.Id);
        DbContext.LocalModelBackends.Add(backend);
        user.SetChatModel(backend.Id);
        await SeedSessionAsync("Session One", null);
        AuthAsSeededUser();
        var cut = Bunit.RenderComponent<Chat>();
        var status = cut.WaitForElement(".beacon-chat-model-status", TimeSpan.FromSeconds(10));
        var notLoaded = Bunit.Services.GetRequiredService<IStringLocalizer<Web>>()["ChatModelNotLoaded"].Value;
        Assert.Equal(string.Format(notLoaded, "test-model"), status.TextContent);
        Assert.DoesNotContain("ModelStatusLabel", status.TextContent);
    }

    private void AuthAsSeededUser()
    {
        var user = DbContext.Users.Single(u => u.Login == "tester");
        var project = DbContext.Projects.Single(p => p.Name == "TestProject");
        Bunit.Services.GetRequiredService<FakeAuthenticationStateProvider>().TriggerAuthenticationStateChanged(
            "tester",
            [],
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim("isAdmin", "true"),
                new Claim("project_id", project.Id.ToString())
            ],
            "TestAuth");
    }

    private async Task<Guid> SeedSessionAsync(string title, string? partBody)
    {
        var project = DbContext.Projects.Single(p => p.Name == "TestProject");
        var session = ChatSession.Create(project.Id, Guid.NewGuid(), title, @"C:\tmp");
        DbContext.ChatSessions.Add(session);
        if (partBody is not null)
            DbContext.ChatParts.Add(ChatPart.Create(session.Id, project.Id, "assistant", "text", partBody, 0));
        await DbContext.SaveChangesAsync();
        return session.Id;
    }
}
