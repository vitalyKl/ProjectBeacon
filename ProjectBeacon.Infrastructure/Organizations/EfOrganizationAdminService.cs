namespace ProjectBeacon.Infrastructure.Organizations;

using System.Net.Mail;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Auth;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Mail;
using ProjectBeacon.Application.Organizations;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Application.Security;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;

public sealed class EfOrganizationAdminService(
    IBeaconDbFactory dbFactory,
    IEmailSender emailSender,
    IConfiguration configuration,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    IUserSessionVersionCache? sessionVersions = null) : IOrganizationAdminService
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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var canReadUsers = actor.HasPermission(OperatorPermission.UsersRead);
        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        IQueryable<Org> query = db.Orgs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var nameSearch = search.ToLowerInvariant();
            if (canReadUsers)
            {
                var emailSearch = nameSearch;
                var matchingMemberOwnerOrgs =
                    from member in db.OrgMembers.IgnoreQueryFilters()
                    join user in db.Users on member.UserId equals user.Id
                    where member.Role == MemberRole.Owner && user.Email.ToLower().Contains(emailSearch)
                    select member.OrgId;
                var matchingPendingOwnerOrgs = db.OrgInvites.IgnoreQueryFilters()
                    .Where(invite => invite.Role == MemberRole.Owner
                        && invite.Status == InviteStatus.Pending
                        && invite.ExpiredAt >= now
                        && invite.Email.ToLower().Contains(emailSearch))
                    .Select(invite => invite.OrgId);
                var matchingOwnerOrgs = matchingMemberOwnerOrgs.Union(matchingPendingOwnerOrgs);
                query = query.Where(org => org.Name.ToLower().Contains(nameSearch)
                    || matchingOwnerOrgs.Contains(org.Id));
            }
            else
            {
                query = query.Where(org => org.Name.ToLower().Contains(nameSearch));
            }
        }

        var total = await query.LongCountAsync(ct);
        var orgRows = await query
            .OrderByDescending(org => org.CreatedAt)
            .ThenBy(org => org.Id)
            .Skip(checked((page - 1) * pageSize))
            .Take(pageSize)
            .Select(org => new { org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt, org.LifecycleStatus })
            .ToListAsync(ct);

        var ids = orgRows.Select(x => x.Id).ToArray();
        var memberRows = new List<MemberAggregate>();
        var projectRows = new List<CountAggregate>();
        var inviteRows = new List<CountAggregate>();
        var pendingOwnerRows = new List<OwnerEmailAggregate>();
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
                    canReadUsers
                        ? groupRows.Where(x => x.member.Role == MemberRole.Owner)
                            .OrderBy(x => x.member.JoinedAt)
                            .Select(x => x.user.Email)
                            .FirstOrDefault()
                        : null))
                .ToListAsync(ct);

            projectRows = await db.Projects.IgnoreQueryFilters()
                .Where(project => ids.Contains(project.OrgId))
                .GroupBy(project => project.OrgId)
                .Select(groupRows => new CountAggregate(groupRows.Key, groupRows.Count()))
                .ToListAsync(ct);

            inviteRows = await db.OrgInvites.IgnoreQueryFilters()
                .Where(invite => ids.Contains(invite.OrgId)
                    && invite.Status == InviteStatus.Pending && invite.ExpiredAt >= now)
                .GroupBy(invite => invite.OrgId)
                .Select(groupRows => new CountAggregate(groupRows.Key, groupRows.Count()))
                .ToListAsync(ct);

            if (canReadUsers)
            {
                pendingOwnerRows = await db.OrgInvites.IgnoreQueryFilters()
                    .Where(invite => ids.Contains(invite.OrgId)
                        && invite.Role == MemberRole.Owner
                        && invite.Status == InviteStatus.Pending
                        && invite.ExpiredAt >= now)
                    .GroupBy(invite => invite.OrgId)
                    .Select(groupRows => new OwnerEmailAggregate(
                        groupRows.Key,
                        groupRows.OrderBy(invite => invite.CreatedAt)
                            .Select(invite => invite.Email)
                            .FirstOrDefault()))
                    .ToListAsync(ct);
            }
        }

        var membersByOrg = memberRows.ToDictionary(x => x.OrgId);
        var projectsByOrg = projectRows.ToDictionary(x => x.OrgId, x => x.Count);
        var invitesByOrg = inviteRows.ToDictionary(x => x.OrgId, x => x.Count);
        var pendingOwnerByOrg = pendingOwnerRows.ToDictionary(x => x.OrgId, x => x.Email);
        var items = orgRows.Select(org =>
        {
            membersByOrg.TryGetValue(org.Id, out var members);
            projectsByOrg.TryGetValue(org.Id, out var projects);
            invitesByOrg.TryGetValue(org.Id, out var invites);
            pendingOwnerByOrg.TryGetValue(org.Id, out var pendingOwnerEmail);
            return new OrganizationAdminListItem(org.Id, org.Name, org.Description,
                org.CreatedAt, org.UpdatedAt, members?.OwnerEmail ?? pendingOwnerEmail,
                members?.Count ?? 0, projects, invites, org.LifecycleStatus);
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

        var canReadUsers = actor.HasPermission(OperatorPermission.UsersRead);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var memberCount = await db.OrgMembers.IgnoreQueryFilters()
            .CountAsync(member => member.OrgId == orgId, ct);
        var pendingInviteCount = await db.OrgInvites.IgnoreQueryFilters()
            .CountAsync(invite => invite.OrgId == orgId
                && invite.Status == InviteStatus.Pending && invite.ExpiredAt >= now, ct);

        var members = new List<OrganizationAdminMember>();
        var invites = new List<OrganizationAdminInvite>();
        if (canReadUsers)
        {
            members = await (
                from member in db.OrgMembers.IgnoreQueryFilters()
                join user in db.Users on member.UserId equals user.Id
                where member.OrgId == orgId
                orderby member.Role, member.JoinedAt
                select new OrganizationAdminMember(
                    user.Id, user.Login, user.Email, member.Role.ToString(),
                    user.DisabledAt != null, user.IsAdmin, member.JoinedAt))
                .ToListAsync(ct);

            invites = await db.OrgInvites.IgnoreQueryFilters()
                .Where(invite => invite.OrgId == orgId)
                .OrderByDescending(invite => invite.CreatedAt)
                .Take(100)
                .Select(invite => new OrganizationAdminInvite(
                    invite.Id, invite.Email, invite.Role.ToString(),
                    invite.Status == InviteStatus.Pending && invite.ExpiredAt < now
                        ? nameof(InviteStatus.Expired) : invite.Status.ToString(),
                    invite.CreatedAt, invite.ExpiredAt))
                .ToListAsync(ct);
        }

        var projectRows = await db.Projects.IgnoreQueryFilters()
            .Where(project => project.OrgId == orgId)
            .OrderByDescending(project => project.UpdatedAt ?? project.CreatedAt)
            .ThenBy(project => project.Id)
            .Take(100)
            .Select(project => new OrganizationAdminProject(
                project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt))
            .ToListAsync(ct);
        var projectCount = await db.Projects.IgnoreQueryFilters()
            .CountAsync(project => project.OrgId == orgId, ct);

        // Workstation daemons are user-owned, not organization-owned. Show only devices
        // belonging to this organization's members, and only to operators with UsersRead.
        var devices = new List<OrganizationAdminDevice>();
        var deviceCount = 0;
        if (canReadUsers && members.Count > 0)
        {
            var memberUserIds = members.Select(member => member.UserId).Distinct().ToArray();
            deviceCount = await db.DaemonDevices
                .CountAsync(device => memberUserIds.Contains(device.UserId), ct);

            var deviceRows = await (
                from device in db.DaemonDevices
                join user in db.Users on device.UserId equals user.Id
                where memberUserIds.Contains(device.UserId)
                orderby device.LastHeartbeatAt descending, device.CreatedAt descending
                select new
                {
                    device.Id,
                    device.Name,
                    device.CreatedAt,
                    device.LastHeartbeatAt,
                    device.RevokedAt,
                    user.Login,
                    user.Email
                })
                .Take(100)
                .ToListAsync(ct);

            devices = deviceRows.Select(device => new OrganizationAdminDevice(
                device.Id,
                device.Name,
                device.Login,
                device.Email,
                device.CreatedAt,
                device.LastHeartbeatAt,
                device.RevokedAt is not null
                    ? "Revoked"
                    : device.LastHeartbeatAt is { } heartbeat
                        && now - heartbeat <= TimeSpan.FromSeconds(ProjectBeacon.Domain.Entities.Devices.DaemonDevice.OnlineWindowSeconds)
                        ? "Online"
                        : "Offline")).ToList();
        }

        var ownerEmail = members.FirstOrDefault(x => x.Role == nameof(MemberRole.Owner))?.Email
            ?? invites.Where(x => x.Role == nameof(MemberRole.Owner) && x.Status == nameof(InviteStatus.Pending))
                .OrderBy(x => x.CreatedAt).Select(x => x.Email).FirstOrDefault();
        var summary = new OrganizationAdminListItem(
            org.Id, org.Name, org.Description, org.CreatedAt, org.UpdatedAt,
            ownerEmail, memberCount, projectCount, pendingInviteCount, org.LifecycleStatus);

        return Result.Ok(new OrganizationAdminDetails(
            summary, members, invites, projectCount, projectRows, deviceCount, devices));
    }

    public async Task<Result<OperatorInviteCreated>> CreateAsync(
        AdminActor actor, CreateOrganizationWithOwnerRequest request, CancellationToken ct = default)
    {
        if (!CanManage(actor))
            return Result.Failure<OperatorInviteCreated>("Organization and user management permissions are required.", ErrorKind.Forbidden);
        if (!actor.IsElevatedAt(timeProvider.GetUtcNow()))
            return Result.Failure<OperatorInviteCreated>("Recent Control Plane authenticator verification is required.", ErrorKind.Forbidden);
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
        if (!TryInviteUrl(rawToken, out var url, out var urlError))
            return Result.Failure<OperatorInviteCreated>(urlError, ErrorKind.Unavailable);
        var invite = OrgInvite.Create(org.Id, email, MemberRole.Owner, actor.UserId, OpaqueToken.Hash(rawToken), now);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Orgs.Add(org);
        db.OrgInvites.Add(invite);
        auditWriter.Stage(db, Audit(actor, "platform.organization.created", "Org", org.Id, org.Id, now,
            """{"newState":"Created"}"""));
        auditWriter.Stage(db, Audit(actor, "platform.organization.owner_invited", "OrgInvite", invite.Id, org.Id, now,
            """{"newState":"Pending"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await TrySendInviteAsync(email, org.Name, url, ct);
        return Result.Ok(new OperatorInviteCreated(org.Id, invite.Id, email, invite.ExpiredAt, url));
    }

    public async Task<Result<OperatorInviteCreated>> InviteOwnerAsync(
        AdminActor actor, Guid orgId, string email, CancellationToken ct = default)
    {
        if (!CanManage(actor))
            return Result.Failure<OperatorInviteCreated>("Organization and user management permissions are required.", ErrorKind.Forbidden);
        if (!actor.IsElevatedAt(timeProvider.GetUtcNow()))
            return Result.Failure<OperatorInviteCreated>("Recent Control Plane authenticator verification is required.", ErrorKind.Forbidden);
        if (orgId == Guid.Empty)
            return Result.Failure<OperatorInviteCreated>("Organization id is required.");
        email = NormalizeEmail(email);

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        var org = await db.Orgs.FirstOrDefaultAsync(x => x.Id == orgId, ct);
        if (org is null)
            return Result.Failure<OperatorInviteCreated>("Organization not found.", ErrorKind.NotFound);
        if (org.LifecycleStatus != OrganizationLifecycleStatus.Active)
            return Result.Failure<OperatorInviteCreated>(
                "Invitations can only be issued for active organizations.", ErrorKind.Conflict);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql())
        {
            await using var lockCommand = db.Database.GetDbConnection().CreateCommand();
            lockCommand.Transaction = transaction.GetDbTransaction();
            lockCommand.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";
            Add(lockCommand, "lock_key", DbType.String, $"organization-owner-invite:{orgId:N}:{email}");
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

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
        if (!TryInviteUrl(rawToken, out var url, out var urlError))
            return Result.Failure<OperatorInviteCreated>(urlError, ErrorKind.Unavailable);
        var invite = OrgInvite.Create(orgId, email, MemberRole.Owner, actor.UserId, OpaqueToken.Hash(rawToken), now);
        db.OrgInvites.Add(invite);
        auditWriter.Stage(db, Audit(actor, "platform.organization.owner_invited", "OrgInvite",
            invite.Id, orgId, now, """{"newState":"Pending"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await TrySendInviteAsync(email, org.Name, url, ct);
        return Result.Ok(new OperatorInviteCreated(orgId, invite.Id, email, invite.ExpiredAt, url));
    }

    public async Task<Result> RevokeInviteAsync(
        AdminActor actor, Guid orgId, Guid inviteId, CancellationToken ct = default)
    {
        if (!CanManage(actor))
            return Result.Forbidden("Organization and user management permissions are required.");
        if (!actor.IsElevatedAt(timeProvider.GetUtcNow()))
            return Result.Forbidden("Recent Control Plane authenticator verification is required.");
        if (orgId == Guid.Empty || inviteId == Guid.Empty)
            return Result.Failure("Organization and invitation ids are required.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using (var rowLock = db.Database.GetDbConnection().CreateCommand())
        {
            rowLock.Transaction = transaction.GetDbTransaction();
            rowLock.CommandText = db.Database.IsNpgsql()
                ? """
                    SELECT "Id" FROM "OrgInvites"
                    WHERE "Id" = @invite_id AND "OrgId" = @org_id
                    FOR UPDATE
                    """
                : """
                    SELECT "Id" FROM "OrgInvites"
                    WHERE "Id" = @invite_id AND "OrgId" = @org_id
                    """;
            Add(rowLock, "invite_id", DbType.Guid, inviteId);
            Add(rowLock, "org_id", DbType.Guid, orgId);
            var lockedInviteId = await rowLock.ExecuteScalarAsync(ct);
            if (lockedInviteId is null || lockedInviteId is DBNull)
                return Result.Failure("Invitation not found.", ErrorKind.NotFound);
        }

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
        await transaction.CommitAsync(ct);
        return Result.Ok();
    }

    public async Task<Result> ChangeMemberRoleAsync(
        AdminActor actor, Guid orgId, Guid userId, MemberRole role, CancellationToken ct = default)
    {
        if (!CanManage(actor))
            return Result.Forbidden("Organization and user management permissions are required.");
        if (!actor.IsElevatedAt(timeProvider.GetUtcNow()))
            return Result.Forbidden("Recent Control Plane authenticator verification is required.");
        if (orgId == Guid.Empty || userId == Guid.Empty)
            return Result.Failure("Organization and user ids are required.");
        if (!Enum.IsDefined(role))
            return Result.Failure("Member role is invalid.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockOrganizationAsync(db, transaction, orgId, ct))
            return Result.Failure("Organization not found.", ErrorKind.NotFound);

        var member = await db.OrgMembers.IgnoreQueryFilters()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.OrgId == orgId && x.UserId == userId, ct);
        if (member is null)
            return Result.Failure("Organization member not found.", ErrorKind.NotFound);
        if (member.Role == role)
            return Result.Ok();
        if (role == MemberRole.Owner && member.User.DisabledAt is not null)
            return Result.Failure("A disabled account cannot become an organization owner.", ErrorKind.Conflict);

        var previousRole = member.Role;
        if (previousRole == MemberRole.Owner && role != MemberRole.Owner)
        {
            var activeOwnerCount = await (
                from other in db.OrgMembers.IgnoreQueryFilters()
                join user in db.Users on other.UserId equals user.Id
                where other.OrgId == orgId
                    && other.Role == MemberRole.Owner
                    && other.UserId != userId
                    && user.DisabledAt == null
                select other.Id)
                .CountAsync(ct);
            if (activeOwnerCount == 0)
                return Result.Failure(
                    "The last active organization owner cannot be demoted. Assign another active owner first.",
                    ErrorKind.Conflict);
        }

        member.ChangeRole(role);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        auditWriter.Stage(db, Audit(actor, "platform.organization.member_role_changed",
            "OrgMember", member.Id, orgId, now,
            $$"""{"previousRole":"{{previousRole}}","newRole":"{{role}}"}"""));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Ok();
    }

    public async Task<Result> SetAccountDisabledAsync(
        AdminActor actor, Guid orgId, Guid userId, bool disabled, CancellationToken ct = default)
    {
        if (!CanManage(actor))
            return Result.Forbidden("Organization and user management permissions are required.");
        if (!actor.IsElevatedAt(timeProvider.GetUtcNow()))
            return Result.Forbidden("Recent Control Plane authenticator verification is required.");
        if (orgId == Guid.Empty || userId == Guid.Empty)
            return Result.Failure("Organization and user ids are required.");

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockUserAsync(db, transaction, userId, ct))
            return Result.Failure("User not found.", ErrorKind.NotFound);

        var isMember = await db.OrgMembers.IgnoreQueryFilters()
            .AnyAsync(x => x.OrgId == orgId && x.UserId == userId, ct);
        if (!isMember)
            return Result.Failure("User is not a member of this organization.", ErrorKind.NotFound);

        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null)
            return Result.Failure("User not found.", ErrorKind.NotFound);
        if (user.IsAdmin || !string.IsNullOrWhiteSpace(user.OperatorRole))
            return Result.Failure("Platform operator accounts cannot be managed as customer accounts.", ErrorKind.Conflict);
        if (user.IsDisabled == disabled)
            return Result.Ok();

        if (disabled)
        {
            // Lock every organization for which this account is an owner in a stable order.
            // This serializes concurrent owner deactivations so two operators cannot both
            // pass the active-owner check and leave an organization ownerless.
            var ownedOrgIds = await db.OrgMembers.IgnoreQueryFilters()
                .Where(x => x.UserId == userId && x.Role == MemberRole.Owner)
                .Select(x => x.OrgId)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync(ct);
            foreach (var ownedOrgId in ownedOrgIds)
            {
                if (!await LockOrganizationAsync(db, transaction, ownedOrgId, ct))
                    return Result.Failure("An owned organization no longer exists.", ErrorKind.Conflict);
            }

            foreach (var ownedOrgId in ownedOrgIds)
            {
                var activeOwnerCount = await (
                    from member in db.OrgMembers.IgnoreQueryFilters()
                    join owner in db.Users on member.UserId equals owner.Id
                    where member.OrgId == ownedOrgId
                        && member.Role == MemberRole.Owner
                        && member.UserId != userId
                        && owner.DisabledAt == null
                    select member.Id)
                    .CountAsync(ct);
                if (activeOwnerCount == 0)
                    return Result.Failure(
                        "This account is an owner and cannot be disabled until every affected organization has another active owner.",
                        ErrorKind.Conflict);
            }

            user.Disable(timeProvider.GetUtcNow().UtcDateTime);
        }
        else
        {
            user.Enable();
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var action = disabled ? "platform.user.account_disabled" : "platform.user.account_enabled";
        var details = disabled
            ? """{"scope":"global_account","newState":"Disabled"}"""
            : """{"scope":"global_account","newState":"Enabled"}""";
        auditWriter.Stage(db, AuditEvent.Create(
            now, AuditActorKind.Human, actor.UserId, null, null,
            action, "User", userId.ToString(), null, null, AuditOutcome.Success,
            detailsJson: details));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        sessionVersions?.Invalidate(userId);
        return Result.Ok();
    }


    public async Task<Result> SetLifecycleStatusAsync(
        AdminActor actor, Guid orgId, OrganizationLifecycleStatus target, string reason,
        CancellationToken ct = default)
    {
        if (!CanManage(actor))
            return Result.Forbidden("Organization and user management permissions are required.");
        if (!actor.IsElevatedAt(timeProvider.GetUtcNow()))
            return Result.Forbidden("Recent Control Plane authenticator verification is required.");
        if (orgId == Guid.Empty)
            return Result.Failure("Organization id is required.");
        if (!Enum.IsDefined(target))
            return Result.Failure("Organization lifecycle status is invalid.", ErrorKind.Validation);
        if (target == OrganizationLifecycleStatus.Closed
            && !actor.HasPermission(OperatorPermission.SystemMaintenance))
            return Result.Forbidden("Only a SystemAdministrator can permanently close an organization.");

        reason = reason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 500)
            return Result.Failure("A reason between 3 and 500 characters is required.", ErrorKind.Validation);

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockOrganizationAsync(db, transaction, orgId, ct))
            return Result.Failure("Organization not found.", ErrorKind.NotFound);

        var org = await db.Orgs.FirstOrDefaultAsync(item => item.Id == orgId, ct);
        if (org is null)
            return Result.Failure("Organization not found.", ErrorKind.NotFound);

        var previous = org.LifecycleStatus;
        try
        {
            org.ChangeLifecycleStatus(target, timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(ex.Message, ErrorKind.Conflict);
        }

        if (previous == org.LifecycleStatus)
            return Result.Ok();

        var action = target switch
        {
            OrganizationLifecycleStatus.Suspended => "platform.organization.suspended",
            OrganizationLifecycleStatus.Active => "platform.organization.reactivated",
            OrganizationLifecycleStatus.Closed => "platform.organization.closed",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            previousState = previous.ToString(),
            newState = target.ToString(),
            reasonCode = "operator_supplied"
        });
        auditWriter.Stage(db, Audit(actor, action, "Org", org.Id, org.Id,
            timeProvider.GetUtcNow().UtcDateTime, details));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Ok();
    }

    private static async Task<bool> LockUserAsync(
        IBeaconDb db, IDbContextTransaction transaction, Guid userId, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = db.Database.IsNpgsql()
            ? """SELECT "Id" FROM "Users" WHERE "Id" = @user_id FOR UPDATE"""
            : """SELECT "Id" FROM "Users" WHERE "Id" = @user_id""";
        Add(command, "user_id", DbType.Guid, userId);
        var result = await command.ExecuteScalarAsync(ct);
        return result is not null && result is not DBNull;
    }

    private static async Task<bool> LockOrganizationAsync(
        IBeaconDb db, IDbContextTransaction transaction, Guid orgId, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = db.Database.IsNpgsql()
            ? """SELECT "Id" FROM "Orgs" WHERE "Id" = @org_id FOR UPDATE"""
            : """SELECT "Id" FROM "Orgs" WHERE "Id" = @org_id""";
        Add(command, "org_id", DbType.Guid, orgId);
        var result = await command.ExecuteScalarAsync(ct);
        return result is not null && result is not DBNull;
    }

    private static bool CanManage(AdminActor actor) =>
        actor.HasPermission(OperatorPermission.OrganizationsManage | OperatorPermission.UsersManage);

    private bool TryInviteUrl(string rawToken, out string url, out string error)
    {
        url = string.Empty;
        error = string.Empty;
        var baseUrl = LocalAuthOptions.PublicUrl(configuration);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var publicUri)
            || (publicUri.Scheme != Uri.UriSchemeHttps && publicUri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(publicUri.Host)
            || !string.IsNullOrEmpty(publicUri.UserInfo)
            || !string.IsNullOrEmpty(publicUri.Query)
            || !string.IsNullOrEmpty(publicUri.Fragment))
        {
            error = "BEACON_PUBLIC_URL must be configured as an absolute customer application URL before creating invitations.";
            return false;
        }

        var environment = configuration["ASPNETCORE_ENVIRONMENT"];
        if (string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase)
            && publicUri.Scheme != Uri.UriSchemeHttps)
        {
            error = "BEACON_PUBLIC_URL must use HTTPS in Production.";
            return false;
        }

        var configuredControlHost = configuration["Control:Host"]
            ?? Environment.GetEnvironmentVariable("CONTROL_HOST")
            ?? "control.projectbeacon.tech";
        if (string.Equals(publicUri.Host.TrimEnd('.'), configuredControlHost.Trim().TrimEnd('.'),
                StringComparison.OrdinalIgnoreCase))
        {
            error = "BEACON_PUBLIC_URL must point to the customer application host, not the Control Plane host.";
            return false;
        }

        url = $"{baseUrl.TrimEnd('/')}/invite?token={Uri.EscapeDataString(rawToken)}";
        return true;
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

    private static void Add(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static AuditEvent Audit(
        AdminActor actor, string action, string targetType, Guid targetId,
        Guid orgId, DateTime atUtc, string detailsJson) =>
        AuditEvent.Create(atUtc, AuditActorKind.Human, actor.UserId, null, null,
            action, targetType, targetId.ToString(), orgId, null, AuditOutcome.Success,
            detailsJson: detailsJson);

    private sealed record MemberAggregate(Guid OrgId, int Count, string? OwnerEmail);
    private sealed record OwnerEmailAggregate(Guid OrgId, string? Email);
    private sealed record CountAggregate(Guid OrgId, int Count);
}
