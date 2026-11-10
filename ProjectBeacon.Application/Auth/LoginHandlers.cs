namespace ProjectBeacon.Application.Auth;

using Application.Common;
using Application.Admin;
using Application.Audit;
using Application.Entitlements;
using Domain.Entities.Audit;
using Application.Authorization;
using Application.Identity;
using Application.Security;
using Domain.Entities.Identity;
using Domain.Enums;
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
    private readonly IAuditWriter? _auditWriter;

    public LoginHandler(
        IBeaconDbFactory dbFactory,
        IPasswordHasher passwords,
        ITokenIssuer tokens,
        IConfiguration? configuration = null,
        IAuditWriter? auditWriter = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _tokens = tokens;
        _configuration = configuration;
        _auditWriter = auditWriter;
    }

    public Task<Result<LoginResponse>> HandleAsync(LoginCommand command, CancellationToken ct = default) =>
        HandleCoreAsync(command, requireOperator: false, ct);

    public Task<Result<LoginResponse>> HandleOperatorAsync(LoginCommand command, CancellationToken ct = default) =>
        HandleCoreAsync(command, requireOperator: true, ct);

    private async Task<Result<LoginResponse>> HandleCoreAsync(
        LoginCommand command,
        bool requireOperator,
        CancellationToken ct)
    {
        // User accounts and login sessions are platform-global. Keep this explicit rather
        // than inheriting a stale project/org scope from a previous web circuit.
        var globalTenant = new TenantContext();
        globalTenant.Assign(null, null, unscoped: true);
        await using var db = _dbFactory.CreateDbContext(globalTenant);
        var deniedAction = requireOperator
            ? "platform.control_login.denied"
            : "account.login.denied";
        var successAction = requireOperator
            ? "platform.control_login.succeeded"
            : "account.login.succeeded";

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Login == command.Request.Login || u.Email == command.Request.Login, ct);

        if (user is null)
        {
            _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
                deniedAction, "Account", null, outcome: AuditOutcome.Denied,
                detailsJson: """{"reasonCode":"invalid_credentials","authMethod":"password"}"""));
            await db.SaveChangesAsync(ct);
            return Result.Failure<LoginResponse>("Invalid credentials.");
        }

        if (user.IsLockedOut)
        {
            _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
                deniedAction, "User", user.Id.ToString(), outcome: AuditOutcome.Denied,
                detailsJson: """{"reasonCode":"account_locked","authMethod":"password"}"""));
            await db.SaveChangesAsync(ct);
            return Result.Failure<LoginResponse>("Account is temporarily locked. Try again later.");
        }

        if (!_passwords.Verify(command.Request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin();
            _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
                deniedAction, "User", user.Id.ToString(), outcome: AuditOutcome.Denied,
                detailsJson: """{"reasonCode":"invalid_credentials","authMethod":"password"}"""));
            await db.SaveChangesAsync(ct);
            return Result.Failure<LoginResponse>("Invalid credentials.");
        }

        if (user.IsDisabled)
        {
            _auditWriter?.Stage(db, AuditEventFactory.ForSystem(
                deniedAction, "User", user.Id.ToString(), outcome: AuditOutcome.Denied,
                detailsJson: """{"reasonCode":"account_disabled","authMethod":"password"}"""));
            await db.SaveChangesAsync(ct);
            return Result.Failure<LoginResponse>("Invalid credentials.", ErrorKind.Unauthorized);
        }

        if (requireOperator && !IsValidOperatorRole(user.OperatorRole))
        {
            _auditWriter?.Stage(db, AuditEventFactory.ForActor(
                new ActorContext(ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None),
                "platform.control_login.denied",
                "User",
                user.Id.ToString(),
                null,
                null,
                outcome: AuditOutcome.Denied,
                detailsJson: """{"reasonCode":"not_operator","authMethod":"password"}"""));
            await db.SaveChangesAsync(ct);
            return Result.Failure<LoginResponse>("Invalid credentials.", ErrorKind.Unauthorized);
        }

        user.RecordLogin();
        if (!string.IsNullOrEmpty(command.Request.IpAddress))
            db.Sessions.Add(UserSession.Create(user.Id, command.Request.IpAddress));

        _auditWriter?.Stage(db, AuditEventFactory.ForActor(
            new ActorContext(ActorType.Human, user.Id, user.IsAdmin, null, null, null, null, ApiTokenCapability.None),
            successAction, "User", user.Id.ToString(), null, null,
            detailsJson: """{"authMethod":"password","newState":"Authenticated"}"""));
        await db.SaveChangesAsync(ct);

        var (projectId, orgId) = await CurrentProjectLookup.ForUserAsync(db, user.Id, user.IsAdmin, null, ct);
        var secret = _configuration?["JWT:Secret"];
        var token = string.IsNullOrEmpty(secret)
            ? null
            : _tokens.GenerateToken(user, secret, projectId: projectId, orgId: orgId);

        return Result.Ok(new LoginResponse(user.Id, user.Login, user.Email, requireOperator || user.IsAdmin, token, user.SessionVersion));
    }

    private static bool IsValidOperatorRole(string? value) =>
        Enum.TryParse<OperatorRole>(value, ignoreCase: false, out var role) && Enum.IsDefined(role);
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
    private readonly TimeProvider _timeProvider;
    private readonly IAuditWriter? _auditWriter;
    private readonly IEntitlementEvaluator? _entitlements;

    public RegisterHandler(
        IBeaconDbFactory dbFactory,
        IPasswordHasher passwords,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null,
        IAuditWriter? auditWriter = null,
        IEntitlementEvaluator? entitlements = null)
    {
        _dbFactory = dbFactory;
        _passwords = passwords;
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditWriter = auditWriter;
        _entitlements = entitlements;
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
        var now = _timeProvider.GetUtcNow().UtcDateTime;
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
                return Result.Failure<RegisterResponse>("Email does not match invite.", ErrorCodes.InviteEmailMismatch, ErrorKind.Forbidden);
            if (orgInvite is not null && (orgInvite.Status != Domain.Enums.InviteStatus.Pending || orgInvite.ExpiredAt < now))
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
            if (projectInvite is not null && (projectInvite.Status != Domain.Enums.InviteStatus.Pending || projectInvite.ExpiredAt < now))
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
        else
            // Open registration has no tenant membership yet; the account and its
            // platform-level audit event must be committed in an explicit global scope.
            window.Assign(null, null, unscoped: true);

        var user = User.Create(login, email, _passwords.Hash(password));
        await using var writeDb = _dbFactory.CreateDbContext(window);
        await using var transaction = await writeDb.Database.BeginTransactionAsync(ct);

        if (orgInvite is not null)
        {
            await EntitlementCapacityLock.AcquireOrganizationAsync(
                writeDb.Database, transaction, orgInvite.OrgId, ct);
            var lifecycleStatus = await writeDb.Orgs.IgnoreQueryFilters()
                .Where(org => org.Id == orgInvite.OrgId)
                .Select(org => (OrganizationLifecycleStatus?)org.LifecycleStatus)
                .FirstOrDefaultAsync(ct);
            if (lifecycleStatus != OrganizationLifecycleStatus.Active)
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");

            var invite = await writeDb.OrgInvites.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == orgInvite.Id, ct);
            if (invite is null || !invite.TryAccept(now))
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");

            var alreadyMember = await writeDb.OrgMembers.IgnoreQueryFilters()
                .AnyAsync(member => member.OrgId == invite.OrgId && member.UserId == user.Id, ct);
            if (!alreadyMember)
            {
                var memberCount = await writeDb.OrgMembers.IgnoreQueryFilters()
                    .LongCountAsync(member => member.OrgId == invite.OrgId, ct);
                var otherPendingInvites = await writeDb.OrgInvites.IgnoreQueryFilters()
                    .LongCountAsync(pending => pending.OrgId == invite.OrgId
                        && pending.Id != invite.Id
                        && pending.Status == Domain.Enums.InviteStatus.Pending
                        && pending.ExpiredAt >= now, ct);
                if (_entitlements is not null)
                {
                    var evaluation = await _entitlements.EvaluateAsync(
                        invite.OrgId,
                        new EntitlementRequirement(
                            EntitlementFeatureKeys.Core,
                            EntitlementLimitKeys.Members,
                            RequestedUnits: 1,
                            CurrentUsage: memberCount + otherPendingInvites),
                        ct);
                    if (!evaluation.Success)
                        return Result.Failure<RegisterResponse>(
                            "Organization member capacity could not be verified.", ErrorKind.Unavailable);
                    var decision = evaluation.Value;
                    if (decision is null || !decision.Allowed)
                    {
                        if (decision?.ReasonCode == "limit_exceeded")
                            return Result.Failure<RegisterResponse>(
                                "The organization member limit has been reached.",
                                "entitlement_limit_exceeded", ErrorKind.Conflict);
                        return Result.Failure<RegisterResponse>(
                            "The organization is not entitled to add members.",
                            "entitlement_feature_required", ErrorKind.Forbidden);
                    }
                }

                writeDb.OrgMembers.Add(OrgMember.Create(invite.OrgId, user.Id, invite.Role));
            }

            writeDb.Users.Add(user);
            var registeredActor = new ActorContext(
                ActorType.Human, user.Id, false, null, null, null, invite.OrgId, ApiTokenCapability.None);
            _auditWriter?.Stage(writeDb, AuditEventFactory.ForActor(
                registeredActor,
                "org.invite.accepted",
                "OrgInvite",
                invite.Id.ToString(),
                invite.OrgId,
                null,
                detailsJson: """{"newState":"Accepted"}"""));
            _auditWriter?.Stage(writeDb, AuditEventFactory.ForActor(
                registeredActor,
                "account.registered",
                "User",
                user.Id.ToString(),
                invite.OrgId,
                null,
                detailsJson: """{"newState":"Active"}"""));
        }

        if (projectInvite is not null)
        {
            await EntitlementCapacityLock.AcquireOrganizationAsync(
                writeDb.Database, transaction, projectOrgId!.Value, ct);
            var lifecycleStatus = await writeDb.Orgs.IgnoreQueryFilters()
                .Where(org => org.Id == projectOrgId.Value)
                .Select(org => (OrganizationLifecycleStatus?)org.LifecycleStatus)
                .FirstOrDefaultAsync(ct);
            if (lifecycleStatus != OrganizationLifecycleStatus.Active)
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");

            var invite = await writeDb.ProjectInvites.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == projectInvite.Id, ct);
            if (invite is null || !invite.TryAccept(now))
                return Result.Failure<RegisterResponse>("Invite is no longer valid.");
            var project = await writeDb.Projects.IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == projectInvite.ProjectId, ct);
            if (project is null || project.OrgId != projectOrgId.Value)
                return Result.Failure<RegisterResponse>("Project not found.");

            var alreadyOrganizationMember = await writeDb.OrgMembers.IgnoreQueryFilters()
                .AnyAsync(member => member.OrgId == project.OrgId && member.UserId == user.Id, ct);
            if (!alreadyOrganizationMember)
            {
                var memberCount = await writeDb.OrgMembers.IgnoreQueryFilters()
                    .LongCountAsync(member => member.OrgId == project.OrgId, ct);
                var pendingOrgInvites = await writeDb.OrgInvites.IgnoreQueryFilters()
                    .LongCountAsync(pending => pending.OrgId == project.OrgId
                        && pending.Status == Domain.Enums.InviteStatus.Pending
                        && pending.ExpiredAt >= now
                        && pending.Email != email, ct);
                if (_entitlements is not null)
                {
                    var evaluation = await _entitlements.EvaluateAsync(
                        project.OrgId,
                        new EntitlementRequirement(
                            EntitlementFeatureKeys.Core,
                            EntitlementLimitKeys.Members,
                            RequestedUnits: 1,
                            CurrentUsage: memberCount + pendingOrgInvites),
                        ct);
                    if (!evaluation.Success)
                        return Result.Failure<RegisterResponse>(
                            "Organization member capacity could not be verified.", ErrorKind.Unavailable);
                    var decision = evaluation.Value;
                    if (decision is null || !decision.Allowed)
                    {
                        if (decision?.ReasonCode == "limit_exceeded")
                            return Result.Failure<RegisterResponse>(
                                "The organization member limit has been reached.",
                                "entitlement_limit_exceeded", ErrorKind.Conflict);
                        return Result.Failure<RegisterResponse>(
                            "The organization is not entitled to add members.",
                            "entitlement_feature_required", ErrorKind.Forbidden);
                    }
                }

                writeDb.OrgMembers.Add(OrgMember.Create(
                    project.OrgId, user.Id, Domain.Enums.MemberRole.Member));
            }

            var duplicateOrgInvites = await writeDb.OrgInvites.IgnoreQueryFilters()
                .Where(pending => pending.OrgId == project.OrgId
                    && pending.Email == email
                    && pending.Status == Domain.Enums.InviteStatus.Pending)
                .ToListAsync(ct);
            foreach (var duplicate in duplicateOrgInvites)
            {
                duplicate.Expire();
                var registeredActor = new ActorContext(
                    ActorType.Human, user.Id, false, null, null,
                    projectInvite.ProjectId, project.OrgId, ApiTokenCapability.None);
                _auditWriter?.Stage(writeDb, AuditEventFactory.ForActor(
                    registeredActor,
                    "org.invite.expired",
                    "OrgInvite",
                    duplicate.Id.ToString(),
                    project.OrgId,
                    null,
                    detailsJson: """{"newState":"Expired","reasonCode":"project_invite_accepted"}"""));
            }

            if (!await writeDb.ProjectMembers.IgnoreQueryFilters()
                    .AnyAsync(member => member.ProjectId == projectInvite.ProjectId && member.UserId == user.Id, ct))
                writeDb.ProjectMembers.Add(Domain.Entities.Projects.ProjectMember.Create(
                    projectInvite.ProjectId, user.Id, invite.Role));

            writeDb.Users.Add(user);
            var projectRegisteredActor = new ActorContext(
                ActorType.Human, user.Id, false, null, null,
                projectInvite.ProjectId, project.OrgId, ApiTokenCapability.None);
            _auditWriter?.Stage(writeDb, AuditEventFactory.ForActor(
                projectRegisteredActor,
                "project.invite.accepted",
                "ProjectInvite",
                invite.Id.ToString(),
                project.OrgId,
                project.Id,
                detailsJson: """{"newState":"Accepted"}"""));
            _auditWriter?.Stage(writeDb, AuditEventFactory.ForActor(
                projectRegisteredActor,
                "account.registered",
                "User",
                user.Id.ToString(),
                project.OrgId,
                project.Id,
                detailsJson: """{"newState":"Active"}"""));
        }

        if (orgInvite is null && projectInvite is null)
        {
            var registeredActor = new ActorContext(
                ActorType.Human, user.Id, false, null, null, null, null, ApiTokenCapability.None);
            writeDb.Users.Add(user);
            _auditWriter?.Stage(writeDb, AuditEventFactory.ForActor(
                registeredActor,
                "account.registered",
                "User",
                user.Id.ToString(),
                null,
                null,
                detailsJson: """{"newState":"Active","source":"open-registration"}"""));
        }

        await writeDb.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Ok(new RegisterResponse(user.Id, user.Login, user.Email));
    }
}
