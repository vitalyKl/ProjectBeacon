namespace ProjectBeacon.Application.Identity;

using Application.Auth;
using Application.Common;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public class CreateOrgInviteHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly IEmailSender _email;
    private readonly IConfiguration? _configuration;

    public CreateOrgInviteHandler(
        IDbContextFactory<BeaconDbContext> dbFactory,
        IEmailSender email,
        IConfiguration? configuration = null)
    {
        _dbFactory = dbFactory;
        _email = email;
        _configuration = configuration;
    }

    public async Task<Result<InviteCreatedDto>> HandleAsync(CreateOrgInviteRequest request, CancellationToken ct = default)
    {
        var email = OrgInvite.NormalizeEmail(request.Email);
        if (email.Length == 0)
            return Result.Failure<InviteCreatedDto>("Email is required.");

        await using var db = _dbFactory.CreateDbContext();
        if (!await MembershipAuth.CanManageOrgAsync(db, request.OrgId, request.ActorUserId, request.ActorIsAdmin, ct))
            return Result.Failure<InviteCreatedDto>("Forbidden.");

        var org = await db.Orgs.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.Id == request.OrgId, ct);
        if (org is null)
            return Result.Failure<InviteCreatedDto>("Org not found.");

        var pending = await db.OrgInvites.IgnoreQueryFilters()
            .Where(i => i.OrgId == request.OrgId && i.Email == email && i.Status == InviteStatus.Pending)
            .ToListAsync(ct);
        foreach (var existing in pending)
        {
            if (existing.ExpiredAt < DateTime.UtcNow)
                existing.Expire();
            else
                return Result.Failure<InviteCreatedDto>("Invite already pending.");
        }

        var raw = OpaqueToken.Generate(OpaqueToken.InvitePrefix);
        var invite = OrgInvite.Create(request.OrgId, email, request.Role, request.ActorUserId, OpaqueToken.Hash(raw));
        db.OrgInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        var url = InviteMail.Link(_configuration, raw);
        await InviteMail.TrySendAsync(_email, email, org.Name, url, ct);
        return Result.Ok(new InviteCreatedDto(invite.Id, invite.Email, invite.Role, invite.ExpiredAt, raw, url));
    }
}

public class CreateProjectInviteHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;
    private readonly IEmailSender _email;
    private readonly IConfiguration? _configuration;

    public CreateProjectInviteHandler(
        IDbContextFactory<BeaconDbContext> dbFactory,
        IEmailSender email,
        IConfiguration? configuration = null)
    {
        _dbFactory = dbFactory;
        _email = email;
        _configuration = configuration;
    }

    public async Task<Result<InviteCreatedDto>> HandleAsync(CreateProjectInviteRequest request, CancellationToken ct = default)
    {
        var email = ProjectInvite.NormalizeEmail(request.Email);
        if (email.Length == 0)
            return Result.Failure<InviteCreatedDto>("Email is required.");

        await using var db = _dbFactory.CreateDbContext();
        if (!await MembershipAuth.CanManageProjectAsync(db, request.ProjectId, request.ActorUserId, request.ActorIsAdmin, ct))
            return Result.Failure<InviteCreatedDto>("Forbidden.");

        var project = await db.Projects.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct);
        if (project is null)
            return Result.Failure<InviteCreatedDto>("Project not found.");

        var pending = await db.ProjectInvites.IgnoreQueryFilters()
            .Where(i => i.ProjectId == request.ProjectId && i.Email == email && i.Status == InviteStatus.Pending)
            .ToListAsync(ct);
        foreach (var existing in pending)
        {
            if (existing.ExpiredAt < DateTime.UtcNow)
                existing.Expire();
            else
                return Result.Failure<InviteCreatedDto>("Invite already pending.");
        }

        var raw = OpaqueToken.Generate(OpaqueToken.InvitePrefix);
        var invite = ProjectInvite.Create(request.ProjectId, email, request.Role, request.ActorUserId, OpaqueToken.Hash(raw));
        db.ProjectInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        var url = InviteMail.Link(_configuration, raw);
        await InviteMail.TrySendAsync(_email, email, project.Name, url, ct);
        return Result.Ok(new InviteCreatedDto(invite.Id, invite.Email, invite.Role, invite.ExpiredAt, raw, url));
    }
}

public class GetInviteHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public GetInviteHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<InvitePreviewDto>> HandleAsync(string token, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var found = await InviteLookup.FindAsync(db, token, ct);
        if (found is null)
            return Result.Failure<InvitePreviewDto>("Invite not found.");
        return Result.Ok(found);
    }
}

public class AcceptInviteHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public AcceptInviteHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<bool>> HandleAsync(AcceptInviteRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.ActorUserId, ct);
        if (user is null)
            return Result.Failure<bool>("User not found.");

        var hash = OpaqueToken.Hash(request.Token.Trim());
        var orgInvite = await db.OrgInvites.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (orgInvite is not null)
        {
            if (!string.Equals(orgInvite.Email, user.Email, StringComparison.Ordinal))
                return Result.Failure<bool>("Email does not match invite.");
            if (!orgInvite.TryAccept())
                return Result.Failure<bool>("Invite is no longer valid.");
            if (!await db.OrgMembers.IgnoreQueryFilters()
                    .AnyAsync(m => m.OrgId == orgInvite.OrgId && m.UserId == user.Id, ct))
                db.OrgMembers.Add(OrgMember.Create(orgInvite.OrgId, user.Id, orgInvite.Role));
            await db.SaveChangesAsync(ct);
            return Result.Ok(true);
        }

        var projectInvite = await db.ProjectInvites.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (projectInvite is null)
            return Result.Failure<bool>("Invite not found.");
        if (!string.Equals(projectInvite.Email, user.Email, StringComparison.Ordinal))
            return Result.Failure<bool>("Email does not match invite.");
        if (!projectInvite.TryAccept())
            return Result.Failure<bool>("Invite is no longer valid.");

        var project = await db.Projects.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == projectInvite.ProjectId, ct);
        if (project is null)
            return Result.Failure<bool>("Project not found.");
        if (!await db.OrgMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.OrgId == project.OrgId && m.UserId == user.Id, ct))
            db.OrgMembers.Add(OrgMember.Create(project.OrgId, user.Id, MemberRole.Member));
        if (!await db.ProjectMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.ProjectId == projectInvite.ProjectId && m.UserId == user.Id, ct))
            db.ProjectMembers.Add(ProjectMember.Create(projectInvite.ProjectId, user.Id, projectInvite.Role));
        await db.SaveChangesAsync(ct);
        return Result.Ok(true);
    }
}

public class ListOrgInvitesHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListOrgInvitesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<InviteListDto>>> HandleAsync(ListInvitesRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!await MembershipAuth.CanManageOrgAsync(db, request.TargetId, request.ActorUserId, request.ActorIsAdmin, ct))
            return Result.Failure<IList<InviteListDto>>("Forbidden.");

        var items = await db.OrgInvites.IgnoreQueryFilters()
            .Where(i => i.OrgId == request.TargetId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InviteListDto(i.Id, i.Email, i.Role, i.Status.ToString(), i.ExpiredAt, i.CreatedAt))
            .ToListAsync(ct);
        return Result.Ok((IList<InviteListDto>)items);
    }
}

public class ListProjectInvitesHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public ListProjectInvitesHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IList<InviteListDto>>> HandleAsync(ListInvitesRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        if (!await MembershipAuth.CanManageProjectAsync(db, request.TargetId, request.ActorUserId, request.ActorIsAdmin, ct))
            return Result.Failure<IList<InviteListDto>>("Forbidden.");

        var items = await db.ProjectInvites.IgnoreQueryFilters()
            .Where(i => i.ProjectId == request.TargetId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InviteListDto(i.Id, i.Email, i.Role, i.Status.ToString(), i.ExpiredAt, i.CreatedAt))
            .ToListAsync(ct);
        return Result.Ok((IList<InviteListDto>)items);
    }
}

public class RevokeOrgInviteHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public RevokeOrgInviteHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<bool>> HandleAsync(RevokeInviteRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var invite = await db.OrgInvites.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == request.InviteId, ct);
        if (invite is null)
            return Result.Failure<bool>("Invite not found.");
        if (!await MembershipAuth.CanManageOrgAsync(db, invite.OrgId, request.ActorUserId, request.ActorIsAdmin, ct))
            return Result.Failure<bool>("Forbidden.");
        invite.Revoke();
        await db.SaveChangesAsync(ct);
        return Result.Ok(true);
    }
}

public class RevokeProjectInviteHandler
{
    private readonly IDbContextFactory<BeaconDbContext> _dbFactory;

    public RevokeProjectInviteHandler(IDbContextFactory<BeaconDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<bool>> HandleAsync(RevokeInviteRequest request, CancellationToken ct = default)
    {
        await using var db = _dbFactory.CreateDbContext();
        var invite = await db.ProjectInvites.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == request.InviteId, ct);
        if (invite is null)
            return Result.Failure<bool>("Invite not found.");
        if (!await MembershipAuth.CanManageProjectAsync(db, invite.ProjectId, request.ActorUserId, request.ActorIsAdmin, ct))
            return Result.Failure<bool>("Forbidden.");
        invite.Revoke();
        await db.SaveChangesAsync(ct);
        return Result.Ok(true);
    }
}

internal static class InviteLookup
{
    public static async Task<InvitePreviewDto?> FindAsync(BeaconDbContext db, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var hash = OpaqueToken.Hash(token.Trim());
        var orgInvite = await db.OrgInvites.IgnoreQueryFilters()
            .Include(i => i.Org)
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (orgInvite is not null)
        {
            if (orgInvite.Status != InviteStatus.Pending || orgInvite.ExpiredAt < DateTime.UtcNow)
                return null;
            return new InvitePreviewDto("org", orgInvite.OrgId, orgInvite.Org.Name, orgInvite.Email, orgInvite.Role, orgInvite.ExpiredAt);
        }

        var projectInvite = await db.ProjectInvites.IgnoreQueryFilters()
            .Include(i => i.Project)
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (projectInvite is null)
            return null;
        if (projectInvite.Status != InviteStatus.Pending || projectInvite.ExpiredAt < DateTime.UtcNow)
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

    public static async Task TrySendAsync(IEmailSender email, string to, string targetName, string url, CancellationToken ct)
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
