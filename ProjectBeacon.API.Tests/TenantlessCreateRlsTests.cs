namespace ProjectBeacon.API.Tests;

using Application.Auth;
using Application.Authorization;
using Application.Common;
using Application.Identity;
using Application.Mail;
using Application.Persistence;
using Application.Projects;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

/// <summary>
/// RLS integration for the tenantless flows: a user with no project (a fresh registrant,
/// the bootstrap admin) runs CreateOrg / CreateProject / Register through the real runtime
/// role (beacon_app) on a fully-migrated isolated database. The tenant is bound exactly as
/// the isolation middleware binds it for such a user — null project, null org, not unscoped
/// — so the RLS policies, not just the EF query filter, decide whether the seed inserts land.
/// </summary>
[Collection("postgres-serial")]
public sealed class TenantlessCreateRlsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string DbName = "beacon_tenantless_create";

    // xUnit instantiates this class once per test; the isolated-DB setup runs once per
    // process (same pattern as RlsRuntimeRoleTests).
    private static bool _initialized;
    private static string s_adminConn = string.Empty;
    private static string s_appConn = string.Empty;
    private static Guid s_userA, s_userB, s_orgA, s_projA;
    private static string s_orgInviteToken = string.Empty;
    private static string s_projInviteToken = string.Empty;

    private readonly PostgresFixture _postgres;
    private string _adminConn = string.Empty;
    private string _appConn = string.Empty;
    private Guid _userA, _userB, _orgA, _projA;
    private string _orgInviteToken = string.Empty;
    private string _projInviteToken = string.Empty;

    public TenantlessCreateRlsTests(PostgresFixture postgres) => _postgres = postgres;

    public async ValueTask InitializeAsync()
    {
        if (!_postgres.Available) return;

        if (!_initialized)
        {
            var adminConn = WithDatabase(_postgres.ConnectionString, DbName);
            var serverConn = WithDatabase(_postgres.ConnectionString, "postgres");

            await ExecAdminAsync(serverConn, $"DROP DATABASE IF EXISTS {DbName} WITH (FORCE);");
            await ExecAdminAsync(serverConn, $"CREATE DATABASE {DbName};");

            var migrateOptions = new DbContextOptionsBuilder<BeaconDbContext>().UseNpgsql(adminConn).Options;
            await using (var migrateDb = new BeaconDbContext(migrateOptions))
                await migrateDb.Database.MigrateAsync();

            var appConn = PostgresConnection.DeriveAppConnectionString(adminConn);
            await PostgresConnection.EnsureAppRoleAsync(appConn, adminConn);

            await using (var seed = new BeaconDbContext(NewAppOptions(appConn)))
            {
                using (TenantScope.EnterUnscoped())
                {
                    var userA = User.Create("tl-user-a", "tl-a@example.test", "rls-test-password-hash");
                    var userB = User.Create("tl-user-b", "tl-b@example.test", "rls-test-password-hash");
                    seed.Users.Add(userA);
                    seed.Users.Add(userB);
                    await seed.SaveChangesAsync();

                    // orgA is owned by userA; userB has no membership anywhere in it.
                    var orgA = Org.Create("Tenantless Org A");
                    seed.Orgs.Add(orgA);
                    seed.OrgMembers.Add(OrgMember.Create(orgA.Id, userA.Id, MemberRole.Owner));
                    await seed.SaveChangesAsync();

                    var projA = Project.Create("Tenantless Proj A", null, orgA.Id);
                    seed.Projects.Add(projA);
                    seed.ProjectMembers.Add(ProjectMember.Create(projA.Id, userA.Id, MemberRole.Owner));
                    await seed.SaveChangesAsync();

                    var orgInviteRaw = OpaqueToken.Generate(OpaqueToken.InvitePrefix);
                    var projInviteRaw = OpaqueToken.Generate(OpaqueToken.InvitePrefix);
                    seed.OrgInvites.Add(OrgInvite.Create(
                        orgA.Id, "tl-invite-org@example.test", MemberRole.Admin, userA.Id, OpaqueToken.Hash(orgInviteRaw)));
                    seed.ProjectInvites.Add(ProjectInvite.Create(
                        projA.Id, "tl-invite-proj@example.test", MemberRole.Admin, userA.Id, OpaqueToken.Hash(projInviteRaw)));
                    await seed.SaveChangesAsync();

                    s_userA = userA.Id;
                    s_userB = userB.Id;
                    s_orgA = orgA.Id;
                    s_projA = projA.Id;
                    s_orgInviteToken = orgInviteRaw;
                    s_projInviteToken = projInviteRaw;
                }
            }

            s_adminConn = adminConn;
            s_appConn = appConn;
            _initialized = true;
        }

        _adminConn = s_adminConn;
        _appConn = s_appConn;
        _userA = s_userA;
        _userB = s_userB;
        _orgA = s_orgA;
        _projA = s_projA;
        _orgInviteToken = s_orgInviteToken;
        _projInviteToken = s_projInviteToken;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // A fresh registrant or bootstrap admin holds no tenant: the middleware binds
    // (null, null, unscoped: false), and RLS is fail-closed for that session.
    private (BeaconDbFactory Factory, TenantContext Tenant) NewTenantlessSession()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: false);
        var factory = new BeaconDbFactory(NewAppOptions(_appConn), tenant);
        return (factory, tenant);
    }

    private static ActorContext Human(Guid userId) =>
        new(ActorType.Human, userId, false, null, null, null, null, ApiTokenCapability.None);

    // userA has never held a project: org creation must succeed as beacon_app under a
    // fail-closed RLS session, and the caller must become the org owner.
    [Fact]
    public async Task CreateOrg_Tenantless_Succeeds_UnderRls()
    {
        _postgres.RequirePostgres();

        var (factory, tenant) = NewTenantlessSession();

        var result = await new CreateOrgHandler(factory, tenant).HandleAsync(
            new CreateOrgCommand(new CreateOrgRequest("Tenantless New Org", null), Human(_userA)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);

        // The session must be restored to the caller's original fail-closed tenant.
        Assert.Null(tenant.ProjectId);
        Assert.Null(tenant.OrgId);
        Assert.False(tenant.Unscoped);

        await using var admin = new BeaconDbContext(NewAdminOptions(_adminConn));
        Assert.True(await admin.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == result.Value!.Id && m.UserId == _userA && m.Role == MemberRole.Owner,
                TestContext.Current.CancellationToken));
    }

    // userA owns orgA (but holds no project scope): project creation inside their org must
    // succeed as beacon_app, seeding the owner membership and the starter labels.
    [Fact]
    public async Task CreateProject_OrgOwner_Tenantless_Succeeds_UnderRls()
    {
        _postgres.RequirePostgres();

        var (factory, tenant) = NewTenantlessSession();

        var result = await new CreateProjectHandler(factory, null, tenant).HandleAsync(
            new CreateProjectCommand(new CreateProjectRequest("Tenantless New Proj", null, _orgA), Human(_userA)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        Assert.Equal(_orgA, result.Value!.OrgId);
        Assert.Null(tenant.ProjectId);
        Assert.False(tenant.Unscoped);

        await using var admin = new BeaconDbContext(NewAdminOptions(_adminConn));
        Assert.True(await admin.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == result.Value!.Id && m.UserId == _userA && m.Role == MemberRole.Owner,
                TestContext.Current.CancellationToken));
        var labelCount = await admin.Labels.IgnoreQueryFilters()
            .CountAsync(l => l.ProjectId == result.Value!.Id, TestContext.Current.CancellationToken);
        Assert.Equal(5, labelCount);
    }

    // userB has no membership in orgA: project creation there must fail with Forbidden
    // (not an RLS error) and must not create the project.
    [Fact]
    public async Task CreateProject_ForeignOrg_Tenantless_Forbidden_UnderRls()
    {
        _postgres.RequirePostgres();

        var (factory, tenant) = NewTenantlessSession();

        var result = await new CreateProjectHandler(factory, null, tenant).HandleAsync(
            new CreateProjectCommand(new CreateProjectRequest("Tenantless Intruder Proj", null, _orgA), Human(_userB)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ErrorKind.Forbidden, result.Kind);
        Assert.Null(tenant.ProjectId);
        Assert.False(tenant.Unscoped);

        await using var admin = new BeaconDbContext(NewAdminOptions(_adminConn));
        Assert.False(await admin.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.UserId == _userB, TestContext.Current.CancellationToken));
    }

    // Anonymous registration with a pending org invite: the invite row is org-scoped, so the
    // lookup must see it despite the anonymous fail-closed session, and the member insert
    // must land under RLS.
    [Fact]
    public async Task Register_OrgInvite_Tenantless_Succeeds_UnderRls()
    {
        _postgres.RequirePostgres();

        var (factory, tenant) = NewTenantlessSession();

        var result = await new RegisterHandler(factory, new BcryptPasswordHasher(), null).HandleAsync(
            new RegisterCommand(new RegisterRequest(
                "tl-org-invitee", "tl-invite-org@example.test", "password-123", _orgInviteToken)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Null(tenant.ProjectId);
        Assert.False(tenant.Unscoped);

        await using var admin = new BeaconDbContext(NewAdminOptions(_adminConn));
        Assert.True(await admin.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == _orgA && m.UserId == result.Value!.UserId && m.Role == MemberRole.Admin,
                TestContext.Current.CancellationToken));
        Assert.True(await admin.OrgInvites.IgnoreQueryFilters()
            .AnyAsync(i => i.OrgId == _orgA && i.Status == InviteStatus.Accepted,
                TestContext.Current.CancellationToken));
    }

    // Anonymous registration with a pending project invite: the invite row is project-scoped,
    // the project lookup must still resolve, and both member rows must land under RLS.
    [Fact]
    public async Task Register_ProjectInvite_Tenantless_Succeeds_UnderRls()
    {
        _postgres.RequirePostgres();

        var (factory, tenant) = NewTenantlessSession();

        var result = await new RegisterHandler(factory, new BcryptPasswordHasher(), null).HandleAsync(
            new RegisterCommand(new RegisterRequest(
                "tl-proj-invitee", "tl-invite-proj@example.test", "password-123", _projInviteToken)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);

        await using var admin = new BeaconDbContext(NewAdminOptions(_adminConn));
        Assert.True(await admin.ProjectMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.ProjectId == _projA && m.UserId == result.Value!.UserId && m.Role == MemberRole.Admin,
                TestContext.Current.CancellationToken));
        Assert.True(await admin.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.OrgId == _orgA && m.UserId == result.Value!.UserId && m.Role == MemberRole.Member,
                TestContext.Current.CancellationToken));
    }

    // Verification: forgot-password touches only non-tenant tables (Users,
    // PasswordResetTokens), so it must already work under the anonymous fail-closed session.
    [Fact]
    public async Task ForgotPassword_Tenantless_Succeeds_UnderRls()
    {
        _postgres.RequirePostgres();

        var (factory, _) = NewTenantlessSession();

        var sent = new CapturingEmailSender();
        var result = await new ForgotPasswordHandler(factory, sent, StubConfiguration()).HandleAsync(
            new ForgotPasswordRequest("tl-a@example.test"), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("tl-a@example.test", sent.LastTo);
    }

    // Verification: reset-password consumes a token and deactivates sessions; none of those
    // tables are tenant-scoped, so the flow must work under the fail-closed session.
    [Fact]
    public async Task ResetPassword_Tenantless_Succeeds_UnderRls()
    {
        _postgres.RequirePostgres();

        var (factory, _) = NewTenantlessSession();
        var raw = OpaqueToken.Generate(OpaqueToken.ResetPrefix);

        await using (var admin = new BeaconDbContext(NewAdminOptions(_adminConn)))
        {
            using (TenantScope.EnterUnscoped())
            {
                var user = await admin.Users.IgnoreQueryFilters()
                    .FirstAsync(u => u.Id == _userB, TestContext.Current.CancellationToken);
                admin.PasswordResetTokens.Add(PasswordResetToken.Create(user.Id, OpaqueToken.Hash(raw)));
                await admin.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }

        var result = await new ResetPasswordHandler(factory, new BcryptPasswordHasher()).HandleAsync(
            new ResetPasswordRequest(raw, "new-password-456"), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);

        await using var verify = new BeaconDbContext(NewAdminOptions(_adminConn));
        using (TenantScope.EnterUnscoped())
        {
            var user = await verify.Users.IgnoreQueryFilters()
                .FirstAsync(u => u.Id == _userB, TestContext.Current.CancellationToken);
            Assert.True(new BcryptPasswordHasher().Verify("new-password-456", user.PasswordHash));
        }
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public bool IsConfigured => true;

        public string? LastTo { get; private set; }
        public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
        {
            LastTo = to;
            return Task.CompletedTask;
        }
    }

    private static IConfiguration StubConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BEACON_PUBLIC_URL"] = "http://beacon.test"
        }).Build();

    private static DbContextOptions<BeaconDbContext> NewAppOptions(string appConn)
    {
        return new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(appConn)
            .AddInterceptors(new TenantRlsConnectionInterceptor())
            .Options;
    }

    private static DbContextOptions<BeaconDbContext> NewAdminOptions(string adminConn)
    {
        return new DbContextOptionsBuilder<BeaconDbContext>()
            .UseNpgsql(adminConn)
            .Options;
    }

    private static async Task ExecAdminAsync(string serverConn, string sql)
    {
        await using var conn = new NpgsqlConnection(serverConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string WithDatabase(string connStr, string database)
    {
        var parts = connStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(';', parts.Select(p =>
            p.StartsWith("Database=", StringComparison.OrdinalIgnoreCase) ? $"Database={database}" : p));
    }
}
