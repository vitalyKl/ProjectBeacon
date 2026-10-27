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
        var cut = Bunit.RenderComponent<Chat>(parameters => parameters
            .Add(p => p.PollSafetyNet, TimeSpan.FromMilliseconds(150)));
        cut.WaitForElement(".beacon-chat-part", TimeSpan.FromSeconds(10));
        Assert.Contains("recovered-marker", cut.Markup);
    }
    [Fact]
    public async Task ChatDock_PollSurvivesTransientFailure()
    {
        await SeedSessionAsync("Session One", null);
        Bunit.Services.AddTransient<GetChatSessionHandler>(
            sp => new RecoveringGetSessionHandler(sp.GetRequiredService<IBeaconDbFactory>()));
        AuthAsSeededUser();
        Bunit.Services.GetRequiredService<NavigationManager>().NavigateTo("/dashboard");
        var cut = Bunit.RenderComponent<ChatDock>(parameters => parameters
            .Add(p => p.PollSafetyNet, TimeSpan.FromMilliseconds(150)));
        cut.Find(".beacon-chat-fab").Click(new MouseEventArgs());
        cut.WaitForElement(".beacon-chat-part", TimeSpan.FromSeconds(10));
        Assert.Contains("recovered-marker", cut.Markup);
    }

    [Fact]
    public async Task ChatPage_SessionSwitchMidFlight_IsNotRevertedByPoll()
    {
        var sessionB = await SeedSessionAsync("Session B", null);
        await Task.Delay(10);
        var sessionA = await SeedSessionAsync("Session A", "alpha-part");
        DelayedGetSessionHandler? delayed = null;
        Bunit.Services.AddTransient<GetChatSessionHandler>(
            sp => delayed ??= new DelayedGetSessionHandler(sp.GetRequiredService<IBeaconDbFactory>()));
        AuthAsSeededUser();
        var signal = Bunit.Services.GetRequiredService<ChatEventSignal>();
        var cut = Bunit.RenderComponent<Chat>();
        var handler = (DelayedGetSessionHandler)Bunit.Services.GetRequiredService<GetChatSessionHandler>();
        // Settle the component's own async initialization before interacting: the newest
        // session (A) must be selected and its parts loaded, which also guarantees the poll
        // loop is already waiting. Clicking first would race the init on slow machines,
        // where the initial Select(A) would apply after the click.
        var head = cut.WaitForElement(".beacon-chat-session-head", TimeSpan.FromSeconds(10));
        Assert.Contains("Session A", head.TextContent);
        cut.WaitForElement(".beacon-chat-part", TimeSpan.FromSeconds(10));
        // Wake the poll loop and let its fetch start (the handler delays it 100 ms), then
        // switch sessions while the fetch is in flight.
        signal.Signal(sessionA);
        // Wait for the woken iteration to start its delayed fetch so the switch below is
        // guaranteed to land mid-flight; then wait for the fetch to complete so the poll
        // guards have run before asserting.
        await handler.FetchStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var target = cut.FindAll("div.beacon-session-main")
            .Single(e => e.TextContent.Contains("Session B"));
        target.Click(new MouseEventArgs());
        await handler.FetchCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.Contains("Session B", cut.Find(".beacon-chat-session-head").TextContent), TimeSpan.FromSeconds(10));
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
        private readonly TaskCompletionSource _fetchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fetchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FetchStarted => _fetchStarted.Task;
        public Task FetchCompleted => _fetchCompleted.Task;

        public DelayedGetSessionHandler(IBeaconDbFactory dbFactory) : base(dbFactory)
        {
        }

        public override async Task<Result<ChatSessionDto>> HandleAsync(GetChatSessionCommand command, CancellationToken ct = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                _fetchStarted.TrySetResult();
                await Task.Delay(100, ct);
                _fetchCompleted.TrySetResult();
            }
            return await base.HandleAsync(command, ct);
        }
    }
}
