namespace ProjectBeacon.Infrastructure.Organizations;

using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Mail;
using ProjectBeacon.Application.Organizations;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;

public sealed class EfOrganizationAdminService(
    IBeaconDbFactory dbFactory,
    IEmailSender emailSender,
    IConfiguration configuration,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) : IOrganizationAdminService
{
    public async Task<Result<OrganizationAdminPage>> ListAsync(
        AdminActor actor, string? search = null, int page = 1, int pageSize = 25,
        CancellationToken ct = default)
    {
        if (!actor.HasPermission(OperatorPermission.OrganizationsRead))
            return Result.Failure<OrganizationAdminPage>("OrganizationsRead permission is required.", ErrorKind.Forbidden);
        if (page < 1 || pageSize is < 1 or > 100)
            return Result.Failure<OrganizationAdminPage>("Page must be positive and page size must be between 1 and 100.");
        search = search?.Trim();
        if (search?.Length > 200)
            return Result.Failure<OrganizationAdminPage>("Search text is too long.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        IQueryable<Org> query = db.Orgs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var matchingOwnerOrgs =
                from member in db.OrgMembers.IgnoreQueryFilters()
                join user in db.Users on member.UserId equals user.Id
                where member.Role == MemberRole.Owner && user.Email.Contains(search)
                select member.OrgId;
            query = query.Where(org => org.Name.Contains(search) || matchingOwnerOrgs.Contains(org.Id));
        }

        var total = await query.LongCountAsync(ct);
        var orgRows = await query
            .OrderByDescending(org => org.CreatedAt)
            .ThenBy(org => org.Id)
            .Skip(checked((page - 1) * pageSize))
            .Take(pageSize)
            .Select(org => new { org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt })
            .ToListAsync(ct);

        var ids = orgRows.Select(x => x.Id).ToArray();
        var memberRows = new List<MemberAggregate>();
        var projectRows = new List<CountAggregate>();
        var inviteRows = new List<CountAggregate>();
        if (ids.Length > 0)
        {
            memberRows = await (
                from member in db.OrgMembers.IgnoreQueryFilters()
                join user in db.Users on member.UserId equals user.Id
                where ids.Contains(member.OrgId)
                group new { member, user } by member.OrgId into groupRows
                select new MemberAggregate(
                    groupRows.Key,
                    groupRows.Count(),
                    groupRows.Where(x => x.member.Role == MemberRole.Owner)
                        .OrderBy(x => x.member.JoinedAt)
                        .Select(x => x.user.Email)
                        .FirstOrDefault()))
                .ToListAsync(ct);

            projectRows = await db.Projects.IgnoreQueryFilters()
                .Where(project => ids.Contains(project.OrgId))
                .GroupBy(project => project.OrgId)
                .Select(groupRows => new CountAggregate(groupRows.Key, groupRows.Count()))
                .ToListAsync(ct);

            inviteRows = await db.OrgInvites.IgnoreQueryFilters()
                .Where(invite => ids.Contains(invite.OrgId) && invite.Status == InviteStatus.Pending)
                .GroupBy(invite => invite.OrgId)
                .Select(groupRows => new CountAggregate(groupRows.Key, groupRows.Count()))
                .ToListAsync(ct);
        }

        var membersByOrg = memberRows.ToDictionary(x => x.OrgId);
        var projectsByOrg = projectRows.ToDictionary(x => x.OrgId, x => x.Count);
        var invitesByOrg = inviteRows.ToDictionary(x => x.OrgId, x => x.Count);
        var items = orgRows.Select(org =>
        {
            membersByOrg.TryGetValue(org.Id, out var members);
            projectsByOrg.TryGetValue(org.Id, out var projects);
            invitesByOrg.TryGetValue(org.Id, out var invites);
            return new OrganizationAdminListItem(org.Id, org.Name, org.Description,
                org.CreatedAt, org.UpdatedAt, members?.OwnerEmail,
                members?.Count ?? 0, projects, invites);
        }).ToArray();

        return Result.Ok(new OrganizationAdminPage(items, total, page, pageSize));
    }

    public async Task<Result<OrganizationAdminDetails>> GetAsync(
        AdminActor actor, Guid orgId, CancellationToken ct = default)
    {
        if (!actor.HasPermission(OperatorPermission.OrganizationsRead))
            return Result.Failure<OrganizationAdminDetails>("OrganizationsRead permission is required.", ErrorKind.Forbidden);
        if (orgId == Guid.Empty)
            return Result.Failure<OrganizationAdminDetails>("Organization id is required.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == orgId, ct);
        if (org is null)
            return Result.Failure<OrganizationAdminDetails>("Organization not found.", ErrorKind.NotFound);

        var members = await (
            from member in db.OrgMembers.IgnoreQueryFilters()
            join user in db.Users on member.UserId equals user.Id
            where member.OrgId == orgId
            orderby member.Role, member.JoinedAt
            select new OrganizationAdminMember(
                user.Id, user.Login, user.Email, member.Role.ToString(),
                user.DisabledAt != null, member.JoinedAt))
            .ToListAsync(ct);

        var invites = await db.OrgInvites.IgnoreQueryFilters()
            .Where(invite => invite.OrgId == orgId)
            .OrderByDescending(invite => invite.CreatedAt)
            .Take(100)
            .Select(invite => new OrganizationAdminInvite(
                invite.Id, invite.Email, invite.Role.ToString(), invite.Status.ToString(),
                invite.CreatedAt, invite.ExpiredAt))
            .ToListAsync(ct);

        var projectCount = await db.Projects.IgnoreQueryFilters()
            .CountAsync(project => project.OrgId == orgId, ct);
        var ownerEmail = members.FirstOrDefault(x => x.Role == nameof(MemberRole.Owner))?.Email;
        var summary = new OrganizationAdminListItem(
            org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt,
            ownerEmail, members.Count, projectCount,
            invites.Count(x => x.Status == nameof(InviteStatus.Pending)));

        return Result.Ok(new OrganizationAdminDetails(summary, members, invites, projectCount));
    }

    public async Task<Result<OperatorInviteCreated>> CreateAsync(
        AdminActor actor, CreateOrganizationWithOwnerRequest request, CancellationToken ct = default)
    {
        if (!CanManage(actor))
            return Result.Failure<OperatorInviteCreated>("Organization and user management permissions are required.", ErrorKind.Forbidden);
        if (actor.UserId == Guid.Empty)
            return Result.Failure<OperatorInviteCreated>("A verified operator identity is required.", ErrorKind.Forbidden);

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            return Result.Failure<OperatorInviteCreated>("Organization name must contain 1 to 200 characters.");
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description?.Length > 2000)
            return Result.Failure<OperatorInviteCreated>("Organization description is too long.");
        var email = NormalizeEmail(request.OwnerEmail);

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var org = Org.Create(name, description);
        var rawToken = OpaqueToken.Generate(OpaqueToken.InvitePrefix);
        var invite = OrgInvite.Create(org.Id, email, MemberRole.Owner, actor.UserId, OpaqueToken.Hash(rawToken), now);

        db.Orgs.Add(org);
        db.OrgInvites.Add(invite);
        auditWriter.Stage(db, Audit(actor, "platform.organization.created", "Org", org.Id, org.Id, now,
            """{"newState":"Created"}"""));
        auditWriter.Stage(db, Audit(actor, "platform.organization.owner_invited", "OrgInvite", invite.Id, org.Id, now,
            """{"newState":"Pending"}"""));
        await db.SaveChangesAsync(ct);

        var url = InviteUrl(rawToken);
        await TrySendInviteAsync(email, org.Name, url, ct);
        return Result.Ok(new OperatorInviteCreated(org.Id, invite.Id, email, invite.ExpiredAt, url));
    }

    public async Task<Result<OperatorInviteCreated>> InviteOwnerAsync(
        AdminActor actor, Guid orgId, string email, CancellationToken ct = default)
    {
        if (!CanManage(actor))
            return Result.Failure<OperatorInviteCreated>("Organization and user management permissions are required.", ErrorKind.Forbidden);
        if (orgId == Guid.Empty)
            return Result.Failure<OperatorInviteCreated>("Organization id is required.");
        email = NormalizeEmail(email);

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        var org = await db.Orgs.FirstOrDefaultAsync(x => x.Id == orgId, ct);
        if (org is null)
            return Result.Failure<OperatorInviteCreated>("Organization not found.", ErrorKind.NotFound);

        var alreadyMember = await (
            from member in db.OrgMembers.IgnoreQueryFilters()
            join user in db.Users on member.UserId equals user.Id
            where member.OrgId == orgId && user.Email == email
            select member.Id).AnyAsync(ct);
        if (alreadyMember)
            return Result.Failure<OperatorInviteCreated>("This user is already a member of the organization.", ErrorKind.Conflict);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var pending = await db.OrgInvites.IgnoreQueryFilters()
            .Where(x => x.OrgId == orgId && x.Email == email && x.Status == InviteStatus.Pending)
            .ToListAsync(ct);
        foreach (var existing in pending)
        {
            if (existing.ExpiredAt >= now)
                return Result.Failure<OperatorInviteCreated>("An invitation for this email is already pending.", ErrorKind.Conflict);
            existing.Expire();
            auditWriter.Stage(db, Audit(actor, "platform.organization.invite_expired", "OrgInvite",
                existing.Id, orgId, now, """{"previousState":"Pending","newState":"Expired"}"""));
        }

        var rawToken = OpaqueToken.Generate(OpaqueToken.InvitePrefix);
        var invite = OrgInvite.Create(orgId, email, MemberRole.Owner, actor.UserId, OpaqueToken.Hash(rawToken), now);
        db.OrgInvites.Add(invite);
        auditWriter.Stage(db, Audit(actor, "platform.organization.owner_invited", "OrgInvite",
            invite.Id, orgId, now, """{"newState":"Pending"}"""));
        await db.SaveChangesAsync(ct);

        var url = InviteUrl(rawToken);
        await TrySendInviteAsync(email, org.Name, url, ct);
        return Result.Ok(new OperatorInviteCreated(orgId, invite.Id, email, invite.ExpiredAt, url));
    }

    public async Task<Result> RevokeInviteAsync(
        AdminActor actor, Guid orgId, Guid inviteId, CancellationToken ct = default)
    {
        if (!CanManage(actor))
            return Result.Forbidden("Organization and user management permissions are required.");
        if (orgId == Guid.Empty || inviteId == Guid.Empty)
            return Result.Failure("Organization and invitation ids are required.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        var invite = await db.OrgInvites.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == inviteId && x.OrgId == orgId, ct);
        if (invite is null)
            return Result.Failure("Invitation not found.", ErrorKind.NotFound);
        if (invite.Status != InviteStatus.Pending)
            return Result.Failure("Only pending invitations can be revoked.", ErrorKind.Conflict);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        invite.Revoke();
        auditWriter.Stage(db, Audit(actor, "platform.organization.invite_revoked", "OrgInvite",
            invite.Id, orgId, now, """{"previousState":"Pending","newState":"Revoked"}"""));
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private static bool CanManage(AdminActor actor) =>
        actor.HasPermission(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);

    private string InviteUrl(string rawToken)
    {
        var baseUrl = LocalAuthOptions.PublicUrl(configuration);
        return string.IsNullOrWhiteSpace(baseUrl)
            ? $"/invite?token={rawToken}"
            : $"{baseUrl.TrimEnd('/')}/invite?token={rawToken}";
    }

    private async Task TrySendInviteAsync(string email, string organizationName, string url, CancellationToken ct)
    {
        try
        {
            await emailSender.SendAsync(email, $"Invitation to {organizationName}",
                $"You were invited to {organizationName} on ProjectBeacon.\nOpen this link to accept:\n{url}\n",
                "org_invite", ct);
        }
        catch
        {
            // The invite is committed and the one-time URL is returned to the operator.
            // SMTP failures are captured by the platform email delivery journal.
        }
    }

    private static string NormalizeEmail(string? raw)
    {
        var email = OrgInvite.NormalizeEmail(raw ?? string.Empty);
        if (email.Length is 0 or > 200
            || !MailAddress.TryCreate(email, out var parsed)
            || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A valid owner email address is required.", nameof(raw));
        return email;
    }

    private static AuditEvent Audit(
        AdminActor actor, string action, string targetType, Guid targetId,
        Guid orgId, DateTime atUtc, string detailsJson) =>
        AuditEvent.Create(atUtc, AuditActorKind.Human, actor.UserId, null, null,
            action, targetType, targetId.ToString(), orgId, null, AuditOutcome.Success,
            detailsJson: detailsJson);

    private sealed record MemberAggregate(Guid OrgId, int Count, string? OwnerEmail);
    private sealed record CountAggregate(Guid OrgId, int Count);
}
