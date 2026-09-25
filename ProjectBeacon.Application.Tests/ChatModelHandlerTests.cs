namespace ProjectBeacon.Application.Tests;

using Application.Agents;
using Application.Auth;
using Application.Chat;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;

public sealed class ChatModelHandlerTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly BeaconDbContext _db;

    public ChatModelHandlerTests()
    {
        (_connection, _db, var unscoped) = HandlerSqlite.Open();
        unscoped.Dispose();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task EmptyPrompt_UsesTheAccountChatModel()
    {
        Guid userId;
        Guid backendId;
        using (TenantScope.EnterUnscoped())
        {
            var user = User.Create("chat-user", "chat-model@beacon.local", "hash");
            _db.Users.Add(user);
            var backend = LocalModelBackend.Create("qwen", ModelBackendType.LlamaCpp, "llama-server -m q.gguf", 4096, 30, user.Id);
            _db.LocalModelBackends.Add(backend);
            await _db.SaveChangesAsync();
            userId = user.Id;
            backendId = backend.Id;
        }

        var factory = HandlerSqlite.Factory(_connection);
        var saved = await new SetChatModelHandler(factory).HandleAsync(new SetChatModelCommand(new SetChatModelRequest(userId, backendId)));
        Assert.True(saved.Success, saved.Error);
        Assert.Equal(backendId, saved.Value);

        await using var db = factory.CreateDbContext();
        var resolved = await ChatModelSelection.ResolveAsync(db, userId, null, default);
        Assert.Equal("beacon-local/qwen", resolved);
    }
}
