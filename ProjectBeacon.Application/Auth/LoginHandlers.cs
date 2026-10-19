namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Identity;
using Application.Security;
using Domain.Entities.Identity;
using Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
/// <summary>
/// Checks the password and lockout. Unknown user and bad password both return invalid credentials. Success issues a session token.
/// </summary>
public class LoginHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IPasswordHasher _passwords;
    private readonly ITokenIssuer _tokens;
    private readonly IConfiguration? _configuration;

    public LoginHandler(IBeaconDbFactory dbFactory, IPasswordHasher passwords, ITokenIssuer tokens, IConfiguration? configuration = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _tokens = tokens;
        _configuration = configuration;
    }

    public async Task<Result<LoginResponse>> HandleAsync(LoginCommand command, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Login == command.Request.Login || u.Email == command.Request.Login, ct);

        if (user is null)
            return Result.Failure<LoginResponse>("Invalid credentials.");

        if (user.IsLockedOut)
            return Result.Failure<LoginResponse>("Account is temporarily locked. Try again later.");

        if (!_passwords.Verify(command.Request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin();
            await db.SaveChangesAsync(ct);
            return Result.Failure<LoginResponse>("Invalid credentials.");
        }

        user.RecordLogin();
        if (!string.IsNullOrEmpty(command.Request.IpAddress))
        {
            db.Sessions.Add(UserSession.Create(user.Id, command.Request.IpAddress));
        }

        await db.SaveChangesAsync(ct);

        var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(db, user.Id, user.IsAdmin, null, ct);
        var secret = _configuration?["JWT:Secret"];
        var token = string.IsNullOrEmpty(secret)
            ? null
            : _tokens.GenerateToken(user, secret, projectId: projectId, orgId: orgId);

        return Result.Ok(new LoginResponse(user.Id, user.Login, user.Email, user.IsAdmin, token));
    }
}
/// <summary>
/// Creates a user. Password must be at least 8 characters. When invite-only is on, the invite must match the email and still be pending.
/// Invite rows are tenant-scoped, so the token lookup runs on a separate unscoped tenant
/// instance and the acceptance plus membership inserts run on one keyed by the invite's
/// tenant. The caller's scoped tenant is never mutated, so concurrent operations on the
/// same circuit are unaffected.
/// </summary>
public class RegisterHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IPasswordHasher _passwords;
    private readonly IConfiguration? _configuration;

    public RegisterHandler(IBeaconDbFactory dbFactory, IPasswordHasher passwords, IConfiguration? configuration = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _configuration = configuration;
    }

    public async Task<Result<RegisterResponse>> HandleAsync(RegisterCommand command, CancellationToken ct = default)
    {
        var login = command.Request.Login?.Trim() ?? string.Empty;
        var email = command.Request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var password = command.Request.Password ?? string.Empty;
        var inviteToken = command.Request.InviteToken?.Trim();

        if (login.Length == 0 || email.Length == 0)
            return Result.Failure<RegisterResponse>("Login and email are required.");
        if (password.Length < 8)
            return Result.Failure<RegisterResponse>("Password must be at least 8 characters.");

        var inviteOnly = LocalAuthOptions.InviteOnly(_configuration);
        if (inviteOnly && string.IsNullOrEmpty(inviteToken))
            return Result.Failure<RegisterResponse>("Invite required.", ErrorKind.Forbidden);

        // Users are not tenant-scoped, so the uniqueness checks run in the caller's own session.
        await using var db = _dbFactory.CreateDbContext();
        if (await db.Users.AnyAsync(u => u.Login == login, ct))
            return Result.Failure<RegisterResponse>("Login already taken.");
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Result.Failure<RegisterResponse>("Email already registered.");

        Domain.Entities.Identity.OrgInvite? orgInvite = null;
        Domain.Entities.Projects.ProjectInvite? projectInvite = null;
        Guid? projectOrgId = null;
        if (!string.IsNullOrEmpty(inviteToken))
        {
            // The invite row belongs to some tenant the anonymous caller cannot see
            // under the fail-closed session: look the token up on a separate unscoped tenant.
            var lookupTenant = new TenantContext();
            lookupTenant.Assign(null, null, unscoped: true);
            await using var lookupDb = _dbFactory.CreateDbContext(lookupTenant);
            var hash = OpaqueToken.Hash(inviteToken);
            orgInvite = await lookupDb.OrgInvites.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
            projectInvite = orgInvite is null
                ? await lookupDb.ProjectInvites.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
                : null;
            if (orgInvite is null && projectInvite is null)
                return Result.Failure<RegisterResponse>("Invite not found.");
            var inviteEmail = orgInvite?.Email ?? projectInvite!.Email;
            if (!string.Equals(inviteEmail, email, StringComparison.Ordinal))
                return Result.Failure<RegisterResponse>("Email does not match invite.");
            if (orgInvite is not null && (orgInvite.Status != Domain.Enums.InviteStatus.Pending || orgInvite.ExpiredAt < DateTime.UtcNow))
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
            if (projectInvite is not null && (projectInvite.Status != Domain.Enums.InviteStatus.Pending || projectInvite.ExpiredAt < DateTime.UtcNow))
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
            if (projectInvite is not null)
            {
                var project = await lookupDb.Projects.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.Id == projectInvite.ProjectId, ct);
                if (project is null)
                    return Result.Failure<RegisterResponse>("Project not found.");
                projectOrgId = project.OrgId;
            }
        }

        // Accept the invite and insert the memberships. RLS must validate each write
        // against the tenant the row belongs to, so key the window by that tenant
        // instead of running the writes unscoped.
        var window = new TenantContext();
        if (orgInvite is not null)
            window.Assign(null, orgInvite.OrgId, unscoped: false);
        else if (projectInvite is not null)
            window.Assign(projectInvite.ProjectId, projectOrgId, unscoped: false);

        var user = User.Create(login, email, _passwords.Hash(password));
        await using var writeDb = _dbFactory.CreateDbContext(window);
        writeDb.Users.Add(user);

        if (orgInvite is not null)
        {
            var invite = await writeDb.OrgInvites.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == orgInvite.Id, ct);
            if (invite is null || !invite.TryAccept())
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
            if (!await writeDb.OrgMembers.IgnoreQueryFilters()
                    .AnyAsync(m => m.OrgId == invite.OrgId && m.UserId == user.Id, ct))
                writeDb.OrgMembers.Add(OrgMember.Create(invite.OrgId, user.Id, invite.Role));
        }

        if (projectInvite is not null)
        {
            var invite = await writeDb.ProjectInvites.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == projectInvite.Id, ct);
            if (invite is null || !invite.TryAccept())
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
            var project = await writeDb.Projects.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == projectInvite.ProjectId, ct);
            if (project is null)
                return Result.Failure<RegisterResponse>("Project not found.");
            if (!await writeDb.OrgMembers.IgnoreQueryFilters()
                    .AnyAsync(m => m.OrgId == project.OrgId && m.UserId == user.Id, ct))
                writeDb.OrgMembers.Add(OrgMember.Create(project.OrgId, user.Id, Domain.Enums.MemberRole.Member));
            if (!await writeDb.ProjectMembers.IgnoreQueryFilters()
                    .AnyAsync(m => m.ProjectId == projectInvite.ProjectId && m.UserId == user.Id, ct))
                writeDb.ProjectMembers.Add(Domain.Entities.Projects.ProjectMember.Create(
                    projectInvite.ProjectId, user.Id, invite.Role));
        }

        await writeDb.SaveChangesAsync(ct);
        return Result.Ok(new RegisterResponse(user.Id, user.Login, user.Email));
    }
}
