namespace ProjectBeacon.API.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Application.Chat;
using Domain.Entities.Chat;
using Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using ProjectBeacon.API.Endpoints;
using Application.Common;
using Application.Persistence;
using Microsoft.AspNetCore.Hosting;

[Collection("api-http")]
public sealed class ChatEventsHttpTests
{
    private static async Task<string> AdminJwt(HttpClient client)
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/bootstrap");
        request.Content = JsonContent.Create(new { });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AuthApiFactory.BootstrapToken);
        var bootstrap = await client.SendAsync(request, ct);
        bootstrap.EnsureSuccessStatusCode();
        var bootJson = await bootstrap.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var password = bootJson.GetProperty("password").GetString();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { Login = "admin", Password = password }, ct);
        login.EnsureSuccessStatusCode();
        var json = await login.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return json.GetProperty("token").GetString()!;
    }

    private static async Task<(HttpClient Client, Guid SessionId)> OpenSessionAsync(AuthApiFactory factory)
    {
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var jwt = await AdminJwt(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var org = await client.PostAsJsonAsync("/v1/orgs", new { name = "Org" }, ct);
        org.EnsureSuccessStatusCode();
        var orgId = (await org.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        var project = await client.PostAsJsonAsync("/v1/projects", new { name = "P", orgId }, ct);
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        Guid sessionId;
        using (TenantScope.EnterUnscoped())
        {
            var session = ChatSession.Create(projectId, Guid.NewGuid(), "SSE", "/tmp");
            session.SetExternalSessionId("oc-1");
            db.ChatSessions.Add(session);
            await db.SaveChangesAsync(ct);
            sessionId = session.Id;
        }
        return (client, sessionId);
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct);
        if (line is null)
            throw new InvalidOperationException("SSE stream closed");
        return line;
    }

    [Fact]
    public async Task EventsStream_EmitsReadyEvent()
    {
        await using var factory = new AuthApiFactory();
        var (client, sessionId) = await OpenSessionAsync(factory);
        var ct = TestContext.Current.CancellationToken;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/chat/sessions/{sessionId}/events"), HttpCompletionOption.ResponseHeadersRead, readCts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        Assert.Equal("retry: 5000", await ReadLineAsync(reader, readCts.Token));
        Assert.Equal("", await ReadLineAsync(reader, readCts.Token));
        Assert.Equal("event: ready", await ReadLineAsync(reader, readCts.Token));
        var dataLine = await ReadLineAsync(reader, readCts.Token);
        var data = JsonSerializer.Deserialize<JsonElement>(dataLine["data: ".Length..]);
        Assert.Equal(sessionId, data.GetProperty("sessionId").GetGuid());
        Assert.Equal("Idle", data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task EventsStream_EmitsChangedOnSignal()
    {
        await using var factory = new AuthApiFactory();
        var (client, sessionId) = await OpenSessionAsync(factory);
        var ct = TestContext.Current.CancellationToken;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/chat/sessions/{sessionId}/events"), HttpCompletionOption.ResponseHeadersRead, readCts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        await ReadLineAsync(reader, readCts.Token);
        await ReadLineAsync(reader, readCts.Token);
        Assert.Equal("event: ready", await ReadLineAsync(reader, readCts.Token));
        await ReadLineAsync(reader, readCts.Token);

        // Give the stream loop time to arm its first wait before the signal fires.
        await Task.Delay(500);
        var events = factory.Services.GetRequiredService<ChatEventSignal>();
        events.Signal(sessionId);

        string? line = null;
        for (var i = 0; i < 30; i++)
        {
            line = await ReadLineAsync(reader, readCts.Token);
            if (line == "event: changed")
                break;
        }
        Assert.Equal("event: changed", line);
        var dataLine = await ReadLineAsync(reader, readCts.Token);
        var data = JsonSerializer.Deserialize<JsonElement>(dataLine["data: ".Length..]);
        Assert.Equal(sessionId, data.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task EventsStream_UnknownSession_NotFound()
    {
        await using var factory = new AuthApiFactory();
        var (client, _) = await OpenSessionAsync(factory);
        var response = await client.GetAsync($"/v1/chat/sessions/{Guid.NewGuid()}/events", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task EventsStream_WithoutToken_Unauthorized()
    {
        await using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync($"/v1/chat/sessions/{Guid.NewGuid()}/events", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EventsStream_EmitsChangedAfterReadyWhenSignalRaces()
    {
        var old = ChatEndpoints.EventWaitInterval;
        ChatEndpoints.EventWaitInterval = TimeSpan.FromMilliseconds(200);
        try
        {
            await using var factory = new RacingSessionApiFactory();
            var (client, sessionId) = await OpenSessionAsync(factory);
            var ct = TestContext.Current.CancellationToken;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/chat/sessions/{sessionId}/events"), HttpCompletionOption.ResponseHeadersRead, readCts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            await ReadLineAsync(reader, readCts.Token);
            await ReadLineAsync(reader, readCts.Token);
            Assert.Equal("event: ready", await ReadLineAsync(reader, readCts.Token));
            await ReadLineAsync(reader, readCts.Token);

            // The racing handler signalled before the stream loop armed its first wait, so the
            // version check must surface the missed edge as a changed event, not a heartbeat.
            string? line = null;
            for (var i = 0; i < 30; i++)
            {
                line = await ReadLineAsync(reader, readCts.Token);
                if (line == "event: changed")
                    break;
            }
            Assert.Equal("event: changed", line);
        }
        finally
        {
            ChatEndpoints.EventWaitInterval = old;
        }
    }
    private sealed class RacingSessionHandler : GetChatSessionHandler
    {
        private readonly ChatEventSignal _events;

        public RacingSessionHandler(IBeaconDbFactory dbFactory, ChatEventSignal events) : base(dbFactory)
        {
            _events = events;
        }

        public override async Task<Result<ChatSessionDto>> HandleAsync(GetChatSessionCommand command, CancellationToken ct = default)
        {
            var result = await base.HandleAsync(command, ct);
            if (result.Success)
                _events.Signal(command.Request.SessionId);
            return result;
        }
    }

    private sealed class RacingSessionApiFactory : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddTransient<GetChatSessionHandler, RacingSessionHandler>());
        }
    }
}
