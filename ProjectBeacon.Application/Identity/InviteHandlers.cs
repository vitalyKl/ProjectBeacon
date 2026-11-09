namespace ProjectBeacon.Application.Identity;

using Application.Auth;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Application.Persistence;
using Application.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
/// <summary>
/// Creates a pending org invite and returns the raw token once. A second pending invite for the same email fails. The caller must be allowed to invite.
/// </summary>
public class CreateOrgInviteHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IEmailSender _email;
    private readonly IAuthorizationService _auth;
    private readonly IConfiguration? _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditWriter? _auditWriter;

    public CreateOrgInviteHandler(
        IBeaconDbFactory dbFactory,
        IEmailSender email,
        IAuthorizationService auth,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _email = email;
        _auth = auth;
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditWriter = auditWriter;
    }

    public async Task<Result<InviteCreatedDto>> HandleAsync(CreateOrgInviteRequest request, CancellationToken ct = default)
    {
        var email = OrgInvite.NormalizeEmail(request.Email);
        if (email.Length == 0)
            return Result.Failure<InviteCreatedDto>("Email is required.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateDbContext();
        var check = await _auth.CanAsync(db, request.Actor, ResourceType.Org, AuthAction.Administer, orgId: request.OrgId, ct: ct);
        if (!check.Success)
            return Result.Forbidden<InviteCreatedDto>();

        var org = await db.Orgs.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == request.OrgId, ct);
        if (org is null)
            return Result.Failure<InviteCreatedDto>("Org not found.");

        var pending = await db.OrgInvites.IgnoreQueryFilters()
            .Where(i => i.OrgId == request.OrgId && i.Email == email && i.Status == InviteStatus.Pending)
            .ToListAsync(ct);
        foreach (var existing in pending)
        {
            if (existing.ExpiredAt < now)
            {
                existing.Expire();
                _auditWriter?.Stage(db, AuditEventFactory.ForActor(
                    request.Actor,
                    "org.invite.expired",
                    "OrgInvite",
                    existing.Id.ToString(),
                    org.Id,
                    null,
                    detailsJson: """{"previousState":"Pending","newState":"Expired"}"""));
            }
            else
                return Result.Failure<InviteCreatedDto>("Invite already pending.");
        }

        var raw = OpaqueToken.Generate(OpaqueToken.InvitePrefix);
        var invite = OrgInvite.Create(request.OrgId, email, request.Role, request.Actor.UserId!.Value, OpaqueToken.Hash(raw), now);
        db.OrgInvites.Add(invite);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            request.Actor,
            "org.invite.created",
            "OrgInvite",
            invite.Id.ToString(),
            org.Id,
            null,
            detailsJson: """{"newState":"Pending"}"""));
        await db.SaveChangesAsync(ct);

        var url = InviteMail.Link(_configuration, raw);
        await InviteMail.TrySendAsync(_email, email, org.Name, url, "org_invite", ct);
        return Result.Ok(new InviteCreatedDto(invite.Id, invite.Email, invite.Role, invite.ExpiredAt, raw, url));
    }
}
/// <summary>
/// Creates a pending project invite and returns the raw token once. A second pending invite for the same email fails.
/// </summary>
public class CreateProjectInviteHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IEmailSender _email;
    private readonly IAuthorizationService _auth;
    private readonly IConfiguration? _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditWriter? _auditWriter;

    public CreateProjectInviteHandler(
        IBeaconDbFactory dbFactory,
        IEmailSender email,
        IAuthorizationService auth,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _email = email;
        _auth = auth;
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditWriter = auditWriter;
    }

    public async Task<Result<InviteCreatedDto>> HandleAsync(CreateProjectInviteRequest request, CancellationToken ct = default)
    {
        var email = ProjectInvite.NormalizeEmail(request.Email);
        if (email.Length == 0)
            return Result.Failure<InviteCreatedDto>("Email is required.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateDbContext();
        var check = await _auth.CanAsync(db, request.Actor, ResourceType.Project, AuthAction.Administer, projectId: request.ProjectId, ct: ct);
        if (!check.Success)
            return Result.Forbidden<InviteCreatedDto>();

        var project = await db.Projects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct);
        if (project is null)
            return Result.Failure<InviteCreatedDto>("Project not found.");

        var pending = await db.ProjectInvites.IgnoreQueryFilters()
            .Where(i => i.ProjectId == request.ProjectId && i.Email == email && i.Status == InviteStatus.Pending)
            .ToListAsync(ct);
        foreach (var existing in pending)
        {
            if (existing.ExpiredAt < now)
            {
                existing.Expire();
                _auditWriter?.Stage(db, AuditEventFactory.ForActor(
                    request.Actor,
                    "project.invite.expired",
                    "ProjectInvite",
                    existing.Id.ToString(),
                    project.OrgId,
                    project.Id,
                    detailsJson: """{"previousState":"Pending","newState":"Expired"}"""));
            }
            else
                return Result.Failure<InviteCreatedDto>("Invite already pending.");
        }

        var raw = OpaqueToken.Generate(OpaqueToken.InvitePrefix);
        var invite = ProjectInvite.Create(request.ProjectId, email, request.Role, request.Actor.UserId!.Value, OpaqueToken.Hash(raw), now);
        db.ProjectInvites.Add(invite);
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            request.Actor,
            "project.invite.created",
            "ProjectInvite",
            invite.Id.ToString(),
            project.OrgId,
            project.Id,
            detailsJson: """{"newState":"Pending"}"""));
        await db.SaveChangesAsync(ct);

        var url = InviteMail.Link(_configuration, raw);
        await InviteMail.TrySendAsync(_email, email, project.Name, url, "project_invite", ct);
        return Result.Ok(new InviteCreatedDto(invite.Id, invite.Email, invite.Role, invite.ExpiredAt, raw, url));
    }
}
/// <summary>
/// Previews an invite by its raw token. The lookup ignores tenant filters because the caller is not a member yet.
/// </summary>
public class GetInviteHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly TimeProvider _timeProvider;

    public GetInviteHandler(IBeaconDbFactory dbFactory, TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result<InvitePreviewDto>> HandleAsync(string token, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var found = await InviteLookup.FindAsync(db, token, now, ct);
        if (found is null)
            return Result.Failure<InvitePreviewDto>("Invite not found.");
        return Result.Ok(found);
    }
}
/// <summary>
/// Accepts an invite when the signed-in user's email matches and the invite is still pending.
/// </summary>
public class AcceptInviteHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditWriter? _auditWriter;

    public AcceptInviteHandler(
        IBeaconDbFactory dbFactory,
        TimeProvider? timeProvider = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(AcceptInviteRequest request, CancellationToken ct = default)
    {
        if (request.Actor.UserId is not { } actorUserId)
            return Result.Failure("Authentication is required.");

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Users are global identities, so resolve the signed-in user in the caller's
        // normal context. Only the invite/target-scope lookup below needs to cross tenants.
        await using var userDb = _dbFactory.CreateDbContext();
        var user = await userDb.Users
            .AsNoTracking()
            .Where(u => u.Id == actorUserId)
            .Select(u => new { u.Id, u.Email })
            .FirstOrDefaultAsync(ct);
        if (user is null)
            return Result.Failure("User not found.");

        // The opaque invite token is the authorization proof for locating the target tenant.
        // Re-read the invite in a fresh tenant-bound context before changing any state.
        var lookupTenant = new TenantContext();
        lookupTenant.Assign(null, null, unscoped: true);
        await using var lookupDb = _dbFactory.CreateDbContext(lookupTenant);

        var hash = OpaqueToken.Hash(request.Token.Trim());
        var orgInviteSnapshot = await lookupDb.OrgInvites.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);

        if (orgInviteSnapshot is not null)
        {
            if (!string.Equals(orgInviteSnapshot.Email, user.Email, StringComparison.Ordinal))
                return Result.Failure("Email does not match invite.", ErrorCodes.InviteEmailMismatch, ErrorKind.Forbidden);

            var tenant = new TenantContext();
            tenant.Assign(null, orgInviteSnapshot.OrgId, unscoped: false);
            await using var writeDb = _dbFactory.CreateDbContext(tenant);
            var invite = await writeDb.OrgInvites.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == orgInviteSnapshot.Id, ct);
            if (invite is null || !invite.TryAccept(now))
                return Result.Failure("Invite is no longer valid.");

            if (!await writeDb.OrgMembers.IgnoreQueryFilters()
                    .AnyAsync(m => m.OrgId == invite.OrgId && m.UserId == user.Id, ct))
                writeDb.OrgMembers.Add(OrgMember.Create(invite.OrgId, user.Id, invite.Role));

            _auditWriter?.Stage(writeDb, AuditEventFactory.ForActor(
                request.Actor,
                "org.invite.accepted",
                "OrgInvite",
                invite.Id.ToString(),
                invite.OrgId,
                null,
                detailsJson: """{"newState":"Accepted"}"""));
            await writeDb.SaveChangesAsync(ct);
            return Result.Ok();
        }

        var projectInviteSnapshot = await lookupDb.ProjectInvites.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (projectInviteSnapshot is null)
            return Result.Failure("Invite not found.");
        if (!string.Equals(projectInviteSnapshot.Email, user.Email, StringComparison.Ordinal))
            return Result.Failure("Email does not match invite.", ErrorCodes.InviteEmailMismatch, ErrorKind.Forbidden);

        var projectSnapshot = await lookupDb.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == projectInviteSnapshot.ProjectId, ct);
        if (projectSnapshot is null)
            return Result.Failure("Project not found.");

        var projectTenant = new TenantContext();
        projectTenant.Assign(projectSnapshot.Id, projectSnapshot.OrgId, unscoped: false);
        await using var projectWriteDb = _dbFactory.CreateDbContext(projectTenant);
        var projectInvite = await projectWriteDb.ProjectInvites.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == projectInviteSnapshot.Id, ct);
        if (projectInvite is null || !projectInvite.TryAccept(now))
            return Result.Failure("Invite is no longer valid.");

        var project = await projectWriteDb.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == projectInvite.ProjectId, ct);
        if (project is null)
            return Result.Failure("Project not found.");

        if (!await projectWriteDb.OrgMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.OrgId == project.OrgId && m.UserId == user.Id, ct))
            projectWriteDb.OrgMembers.Add(OrgMember.Create(project.OrgId, user.Id, MemberRole.Member));
        if (!await projectWriteDb.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.ProjectId == projectInvite.ProjectId && m.UserId == user.Id, ct))
            projectWriteDb.ProjectMembers.Add(ProjectMember.Create(projectInvite.ProjectId, user.Id, projectInvite.Role));

        _auditWriter?.Stage(projectWriteDb, AuditEventFactory.ForActor(
            request.Actor,
            "project.invite.accepted",
            "ProjectInvite",
            projectInvite.Id.ToString(),
            project.OrgId,
            project.Id,
            detailsJson: """{"newState":"Accepted"}"""));
        await projectWriteDb.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Lists org invites. Forbidden when the caller cannot administer the org.
/// </summary>
public class ListOrgInvitesHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;

    public ListOrgInvitesHandler(IBeaconDbFactory dbFactory, IAuthorizationService auth)
    {
        _dbFactory = dbFactory;
        _auth = auth;
    }

    public async Task<Result<IList<InviteListDto>>> HandleAsync(ListInvitesRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var check = await _auth.CanAsync(db, request.Actor, ResourceType.Org, AuthAction.Administer, orgId: request.TargetId, ct: ct);
        if (!check.Success)
            return Result.Forbidden<IList<InviteListDto>>();

        var items = await db.OrgInvites.IgnoreQueryFilters()
            .Where(i => i.OrgId == request.TargetId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InviteListDto(i.Id, i.Email, i.Role, i.Status.ToString(), i.ExpiredAt, i.CreatedAt))
            .ToListAsync(ct);
        return Result.Ok((IList<InviteListDto>)items);
    }
}
/// <summary>
/// Lists project invites. Forbidden when the caller cannot administer the project.
/// </summary>
public class ListProjectInvitesHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;

    public ListProjectInvitesHandler(IBeaconDbFactory dbFactory, IAuthorizationService auth)
    {
        _dbFactory = dbFactory;
        _auth = auth;
    }

    public async Task<Result<IList<InviteListDto>>> HandleAsync(ListInvitesRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var check = await _auth.CanAsync(db, request.Actor, ResourceType.Project, AuthAction.Administer, projectId: request.TargetId, ct: ct);
        if (!check.Success)
            return Result.Forbidden<IList<InviteListDto>>();

        var items = await db.ProjectInvites.IgnoreQueryFilters()
            .Where(i => i.ProjectId == request.TargetId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InviteListDto(i.Id, i.Email, i.Role, i.Status.ToString(), i.ExpiredAt, i.CreatedAt))
            .ToListAsync(ct);
        return Result.Ok((IList<InviteListDto>)items);
    }
}
/// <summary>
/// Revokes a pending org invite.
/// </summary>
public class RevokeOrgInviteHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;
    private readonly IAuditWriter? _auditWriter;

    public RevokeOrgInviteHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService auth,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _auth = auth;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(RevokeInviteRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var invite = await db.OrgInvites.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == request.InviteId, ct);
        if (invite is null)
            return Result.Failure("Invite not found.");
        var check = await _auth.CanAsync(db, request.Actor, ResourceType.Org, AuthAction.Administer, orgId: invite.OrgId, ct: ct);
        if (!check.Success)
            return Result.Forbidden();
        invite.Revoke();
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            request.Actor,
            "org.invite.revoked",
            "OrgInvite",
            invite.Id.ToString(),
            invite.OrgId,
            null,
            detailsJson: """{"previousState":"Pending","newState":"Revoked"}"""));
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
/// <summary>
/// Revokes a pending project invite.
/// </summary>
public class RevokeProjectInviteHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _auth;
    private readonly IAuditWriter? _auditWriter;

    public RevokeProjectInviteHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService auth,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _auth = auth;
        _auditWriter = auditWriter;
    }

    public async Task<Result> HandleAsync(RevokeInviteRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var invite = await db.ProjectInvites.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == request.InviteId, ct);
        if (invite is null)
            return Result.Failure("Invite not found.");
        var check = await _auth.CanAsync(db, request.Actor, ResourceType.Project, AuthAction.Administer, projectId: invite.ProjectId, ct: ct);
        if (!check.Success)
            return Result.Forbidden();
        var project = await db.Projects
            .FirstOrDefaultAsync(p => p.Id == invite.ProjectId, ct);
        if (project is null)
            return Result.Failure("Project not found.");

        invite.Revoke();
        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            request.Actor,
            "project.invite.revoked",
            "ProjectInvite",
            invite.Id.ToString(),
            project.OrgId,
            project.Id,
            detailsJson: """{"previousState":"Pending","newState":"Revoked"}"""));
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal static class InviteLookup
{
    public static async Task<InvitePreviewDto?> FindAsync(IBeaconDb db, string token, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var hash = OpaqueToken.Hash(token.Trim());
        var orgInvite = await db.OrgInvites.IgnoreQueryFilters()
            .Include(i => i.Org)
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (orgInvite is not null)
        {
            if (orgInvite.Status != InviteStatus.Pending || orgInvite.ExpiredAt < now)
                return null;
            return new InvitePreviewDto("org", orgInvite.OrgId, orgInvite.Org.Name, orgInvite.Email, orgInvite.Role, orgInvite.ExpiredAt);
        }

        var projectInvite = await db.ProjectInvites.IgnoreQueryFilters()
            .Include(i => i.Project)
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (projectInvite is null)
            return null;
        if (projectInvite.Status != InviteStatus.Pending || projectInvite.ExpiredAt < now)
            return null;
        return new InvitePreviewDto(
            "project", projectInvite.ProjectId, projectInvite.Project.Name,
            projectInvite.Email, projectInvite.Role, projectInvite.ExpiredAt);
    }
}

internal static class InviteMail
{
    public static string Link(IConfiguration? configuration, string token)
    {
        var baseUrl = LocalAuthOptions.PublicUrl(configuration);
        return string.IsNullOrEmpty(baseUrl) ? $"/invite?token={token}" : $"{baseUrl}/invite?token={token}";
    }

    public static async Task TrySendAsync(IEmailSender email, string to, string targetName, string url, string purpose, CancellationToken ct)
    {
        try
        {
            await email.SendAsync(to, $"Invitation to {targetName}",
                $"You were invited to {targetName} on ProjectBeacon.\nOpen this link to accept:\n{url}\n", ct);
        }
        catch
        {
        }
    }
}
