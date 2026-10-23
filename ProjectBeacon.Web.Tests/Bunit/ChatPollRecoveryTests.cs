namespace ProjectBeacon.Web.Tests.Bunit;

using System.Security.Claims;
using global::Bunit;
using global::Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Application.Chat;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Chat;
using ProjectBeacon.Web.Features.Chat;


/// <summary>
/// The chat poll loops must survive transient handler failures (a flaky database call, a circuit
/// hiccup): a loop that dies stops live updates for the whole component. The handler below fails
/// once and then succeeds; the components must keep polling until the recovered part shows up.
/// </summary>
public sealed class ChatPollRecoveryTests : BUnitRenderBase
{
    [Fact]
    public async Task ChatPage_PollSurvivesTransientFailure()
    {
        await SeedSessionAsync("Session One", null);
        Bunit.Services.AddTransient<GetChatSessionHandler>(
            sp => new RecoveringGetSessionHandler(sp.GetRequiredService<IBeaconDbFactory>()));
        AuthAsSeededUser();
        var old = Chat.PollSafetyNet;
        Chat.PollSafetyNet = TimeSpan.FromMilliseconds(150);
        try
        {
            var cut = Bunit.RenderComponent<Chat>();
            cut.WaitForElement(".beacon-chat-part", TimeSpan.FromSeconds(10));
            Assert.Contains("recovered-marker", cut.Markup);
        }
        finally
        {
            Chat.PollSafetyNet = old;
        }
    }
    [Fact]
    public async Task ChatDock_PollSurvivesTransientFailure()
    {
        await SeedSessionAsync("Session One", null);
        Bunit.Services.AddTransient<GetChatSessionHandler>(
            sp => new RecoveringGetSessionHandler(sp.GetRequiredService<IBeaconDbFactory>()));
        AuthAsSeededUser();
        var old = ChatDock.PollSafetyNet;
        ChatDock.PollSafetyNet = TimeSpan.FromMilliseconds(150);
        try
        {
            Bunit.Services.GetRequiredService<NavigationManager>().NavigateTo("/dashboard");
            var cut = Bunit.RenderComponent<ChatDock>();
            cut.Find(".beacon-chat-fab").Click(new MouseEventArgs());
            cut.WaitForElement(".beacon-chat-part", TimeSpan.FromSeconds(10));
            Assert.Contains("recovered-marker", cut.Markup);
        }
        finally
        {
            ChatDock.PollSafetyNet = old;
        }
    }

    [Fact]
    public async Task ChatPage_SessionSwitchMidFlight_IsNotRevertedByPoll()
    {
        var sessionB = await SeedSessionAsync("Session B", null);
        await Task.Delay(10);
        var sessionA = await SeedSessionAsync("Session A", "alpha-part");
        Bunit.Services.AddTransient<GetChatSessionHandler>(
            sp => new DelayedGetSessionHandler(sp.GetRequiredService<IBeaconDbFactory>()));
        AuthAsSeededUser();
        var signal = Bunit.Services.GetRequiredService<ChatEventSignal>();
        var old = Chat.PollSafetyNet;
        Chat.PollSafetyNet = TimeSpan.FromSeconds(5);
        try
        {
            var cut = Bunit.RenderComponent<Chat>();
            // Wake the poll loop and let its fetch start (the handler delays it 100 ms), then
            // switch sessions while the fetch is in flight.
            signal.Signal(sessionA);
            await Task.Yield();
            await Task.Delay(10);
            var target = cut.FindAll("div.beacon-session-main")
                .Single(e => e.TextContent.Contains("Session B"));
            target.Click(new MouseEventArgs());
            await Task.Delay(500);
            Assert.Contains("Session B", cut.Find(".beacon-chat-session-head").TextContent);
        }
        finally
        {
            Chat.PollSafetyNet = old;
        }
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

    private sealed class RecoveringGetSessionHandler : GetChatSessionHandler
    {
        private readonly IBeaconDbFactory _db;
        private bool _recovered;

        public RecoveringGetSessionHandler(IBeaconDbFactory dbFactory) : base(dbFactory)
        {
            _db = dbFactory;
        }

        public override async Task<Result<ChatSessionDto>> HandleAsync(GetChatSessionCommand command, CancellationToken ct = default)
        {
            if (!_recovered)
            {
                _recovered = true;
                await using var db = _db.CreateDbContext();
                var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == command.Request.SessionId, ct);
                if (session is not null)
                {
                    db.ChatParts.Add(ChatPart.Create(session.Id, session.ProjectId, "assistant", "text", "recovered-marker", 0));
                    await db.SaveChangesAsync(ct);
                }
                throw new InvalidOperationException("simulated transient chat failure");
            }
            return await base.HandleAsync(command, ct);
        }
    }

    private sealed class DelayedGetSessionHandler : GetChatSessionHandler
    {
        private bool _delayed;

        public DelayedGetSessionHandler(IBeaconDbFactory dbFactory) : base(dbFactory)
        {
        }

        public override async Task<Result<ChatSessionDto>> HandleAsync(GetChatSessionCommand command, CancellationToken ct = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                await Task.Delay(100, ct);
            }
            return await base.HandleAsync(command, ct);
        }
    }
}
