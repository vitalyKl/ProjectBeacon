namespace ProjectBeacon.Application.Tests;

using Application.Authorization;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;

public sealed partial class AuthorizationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private readonly AuthorizationService _auth;

    public AuthorizationServiceTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
        _auth = new AuthorizationService();
    }

    public void Dispose()
    {
        _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private static ActorContext Worker() =>
        new(ActorType.Worker, null, false, null, null, null, null, ApiTokenCapability.None);

    private static ActorContext Device(Guid id) =>
        new(ActorType.Device, null, false, null, id, null, null, ApiTokenCapability.None);

    private static ActorContext Admin(Guid userId) =>
        new(ActorType.Human, userId, true, null, null, null, null, ApiTokenCapability.None);

    private static ActorContext Human(Guid userId) =>
        new(ActorType.Human, userId, false, null, null, null, null, ApiTokenCapability.None);

    private static ActorContext Token(ApiTokenCapability caps = ApiTokenCapability.None, Guid? projectId = null) =>
        new(ActorType.ApiToken, null, false, Guid.NewGuid(), null, projectId, null, caps);



}


