namespace ProjectBeacon.Infrastructure.Organizations;

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Application.Audit;
using ProjectBeacon.Application.Billing;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Organizations;
using ProjectBeacon.Application.Persistence;
using ProjectBeacon.Domain.Entities.Audit;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Attachments;
using ProjectBeacon.Infrastructure.Data;

public sealed class EfOrganizationDataExportService(
    IBeaconDbFactory dbFactory,
    IAuthorizationService authorization,
    IAuditWriter auditWriter,
    IAttachmentService attachments,
    TimeProvider timeProvider) : IOrganizationDataExportService
{
    private const long MaximumRows = 100_000;
    private const long MaximumAttachmentBytes = 128L * 1024 * 1024;
    private const long MaximumArchiveBytes = 160L * 1024 * 1024;
    private const long MaximumJsonPayloadBytes = 32L * 1024 * 1024;
    private const long MaximumAttachmentLength = 10L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 48,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<bool> CanExportAsync(
        Guid orgId,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (orgId == Guid.Empty || !actor.IsHuman || actor.UserId is null)
            return false;

        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        await using var db = dbFactory.CreateDbContext(tenant);
        return await db.OrgMembers.IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(member => member.OrgId == orgId
                && member.UserId == actor.UserId
                && (member.Role == MemberRole.Owner || member.Role == MemberRole.Admin), ct);
    }

    public async Task<Result<OrganizationDataExport>> ExportAsync(
        Guid orgId,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (orgId == Guid.Empty)
            return Result.Failure<OrganizationDataExport>("Organization id is required.");
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Failure<OrganizationDataExport>(
                "A signed-in organization owner or administrator is required.",
                ErrorKind.Forbidden);

        // First prove access in the organization's normal tenant scope. Project-scoped
        // entities cannot be queried with a null project id, so the actual archive read
        // uses a short-lived operator-style unscoped context below. Every query in that
        // context is then constrained by this explicitly authorized OrgId/projectId set.
        var tenant = new TenantContext();
        tenant.Assign(null, orgId, unscoped: false);
        await using (var authorizationDb = dbFactory.CreateDbContext(tenant))
        {
            var permission = await authorization.CanAsync(
                authorizationDb, actor, ResourceType.Org, AuthAction.Administer, orgId: orgId, ct: ct);
            var isOrganizationAdmin = await authorizationDb.OrgMembers.AnyAsync(
                member => member.OrgId == orgId && member.UserId == actor.UserId
                    && (member.Role == MemberRole.Owner || member.Role == MemberRole.Admin),
                ct);
            if (!permission.Success || !isOrganizationAdmin)
                return Result.Failure<OrganizationDataExport>(
                    "Only an organization owner or administrator can export its data.",
                    ErrorKind.Forbidden);
        }

        using var unscoped = TenantScope.EnterUnscoped();
        await using var db = dbFactory.CreateDbContext();
        var isolation = string.Equals(
            db.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal)
            ? IsolationLevel.RepeatableRead
            : IsolationLevel.Serializable;
        await using var transaction = await db.Database.BeginTransactionAsync(isolation, ct);

        // Repeat authorization inside the export snapshot to close the membership-revocation
        // race between the scoped preflight and the unscoped, explicitly constrained reads.
        var permissionInsideSnapshot = await authorization.CanAsync(
            db, actor, ResourceType.Org, AuthAction.Administer, orgId: orgId, ct: ct);
        var isOrganizationAdminInsideSnapshot = await db.OrgMembers.AsNoTracking()
            .AnyAsync(member => member.OrgId == orgId && member.UserId == actor.UserId
                && (member.Role == MemberRole.Owner || member.Role == MemberRole.Admin), ct);
        if (!permissionInsideSnapshot.Success || !isOrganizationAdminInsideSnapshot)
            return Result.Failure<OrganizationDataExport>(
                "Only an organization owner or administrator can export its data.",
                ErrorKind.Forbidden);

        var organization = await db.Orgs.IgnoreQueryFilters().AsNoTracking()
            .Where(org => org.Id == orgId)
            .Select(org => new
            {
                org.Id,
                org.Name,
                org.Description,
                org.CreatedAt,
                org.UpdatedAt,
                org.LifecycleStatus
            })
            .SingleOrDefaultAsync(ct);
        if (organization is null)
            return Result.Failure<OrganizationDataExport>("Organization not found.", ErrorKind.NotFound);

        var projectCount = await db.Projects.IgnoreQueryFilters()
            .LongCountAsync(project => project.OrgId == orgId, ct);
        if (projectCount > MaximumRows)
            return TooLarge();

        var projectIds = await db.Projects.IgnoreQueryFilters().AsNoTracking()
            .Where(project => project.OrgId == orgId)
            .OrderBy(project => project.CreatedAt)
            .Select(project => project.Id)
            .ToListAsync(ct);
        var billingSubscriptionCount = await CountOrganizationRowsAsync(
            db, "OrganizationSubscriptions", orgId, ct);
        var billingInvoiceCount = await CountOrganizationRowsAsync(
            db, "BillingInvoices", orgId, ct);
        var billingLedgerCount = await CountOrganizationRowsAsync(
            db, "BillingLedgerEntries", orgId, ct);

        var taskIds = db.Tasks.AsNoTracking()
            .Where(task => projectIds.Contains(task.ProjectId))
            .Select(task => task.Id);
        var decisionIds = db.Decisions.AsNoTracking()
            .Where(decision => projectIds.Contains(decision.ProjectId))
            .Select(decision => decision.Id);

        var counts = new long[]
        {
            projectCount,
            await db.OrgMembers.LongCountAsync(member => member.OrgId == orgId, ct),
            await db.OrgInvites.LongCountAsync(invite => invite.OrgId == orgId, ct),
            await db.ProjectMembers.LongCountAsync(member => projectIds.Contains(member.ProjectId), ct),
            await db.ProjectInvites.LongCountAsync(invite => projectIds.Contains(invite.ProjectId), ct),
            await db.Labels.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.LabelPaths.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.Milestones.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.Tasks.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.TaskComments.LongCountAsync(item => taskIds.Contains(item.TaskId), ct),
            await db.TaskDependencies.LongCountAsync(item =>
                taskIds.Contains(item.TaskId) && taskIds.Contains(item.DependentTaskId), ct),
            await db.TaskSteps.LongCountAsync(item =>
                projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId), ct),
            await db.TaskPhases.LongCountAsync(item =>
                projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId), ct),
            await db.TaskWorkspaceSections.LongCountAsync(item =>
                projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId), ct),
            await db.RoleBindings.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.Subtasks.LongCountAsync(item =>
                projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId), ct),
            await db.Reports.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.ContextSections.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.ContextRevisions.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.Decisions.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.DecisionTasks.LongCountAsync(item =>
                decisionIds.Contains(item.DecisionId) && taskIds.Contains(item.TaskId), ct),
            await db.Constraints.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.ChatSessions.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.ChatParts.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            await db.TaskAttachments.LongCountAsync(item => projectIds.Contains(item.ProjectId), ct),
            billingSubscriptionCount,
            billingInvoiceCount,
            billingLedgerCount
        };
        var totalRows = counts.Aggregate(0L, (total, count) => checked(total + count));
        if (totalRows > MaximumRows)
            return TooLarge();

        var members = await (
            from member in db.OrgMembers.AsNoTracking()
            join user in db.Users.AsNoTracking() on member.UserId equals user.Id
            where member.OrgId == orgId
            orderby member.JoinedAt
            select new
            {
                member.UserId,
                user.Login,
                user.Email,
                member.Role,
                member.JoinedAt
            }).ToListAsync(ct);

        var orgInvites = await db.OrgInvites.AsNoTracking()
            .Where(invite => invite.OrgId == orgId)
            .OrderBy(invite => invite.CreatedAt)
            .Select(invite => new
            {
                invite.Id,
                invite.Email,
                invite.Role,
                invite.Status,
                invite.InvitedByUserId,
                invite.AcceptedAt,
                invite.CreatedAt,
                invite.ExpiredAt
            }).ToListAsync(ct);

        var projects = await db.Projects.AsNoTracking()
            .Where(project => projectIds.Contains(project.Id))
            .OrderBy(project => project.CreatedAt)
            .Select(project => new
            {
                project.Id,
                project.Name,
                project.Description,
                project.ReviewCheckCommand,
                project.OrgId,
                project.CreatedAt,
                project.UpdatedAt
            }).ToListAsync(ct);

        var projectMembers = await db.ProjectMembers.AsNoTracking()
            .Where(member => projectIds.Contains(member.ProjectId))
            .OrderBy(member => member.JoinedAt)
            .Select(member => new
            {
                member.ProjectId,
                member.UserId,
                member.Role,
                member.JoinedAt
            }).ToListAsync(ct);

        var projectInvites = await db.ProjectInvites.AsNoTracking()
            .Where(invite => projectIds.Contains(invite.ProjectId))
            .OrderBy(invite => invite.CreatedAt)
            .Select(invite => new
            {
                invite.Id,
                invite.ProjectId,
                invite.Email,
                invite.Role,
                invite.Status,
                invite.InvitedByUserId,
                invite.AcceptedAt,
                invite.CreatedAt,
                invite.ExpiredAt
            }).ToListAsync(ct);

        var labels = await db.Labels.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.Name)
            .Select(item => new { item.Id, item.ProjectId, item.Name, item.Color, item.PathPrefix })
            .ToListAsync(ct);

        var labelPaths = await db.LabelPaths.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.Path)
            .Select(item => new { item.Id, item.ProjectId, item.LabelId, item.Path })
            .ToListAsync(ct);

        var milestones = await db.Milestones.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.Order)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Name,
                item.Description,
                item.Order,
                item.CreatedAt,
                item.UpdatedAt,
                item.ClosedAt
            }).ToListAsync(ct);

        var tasks = await db.Tasks.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Title,
                item.Description,
                item.Status,
                item.Priority,
                item.Type,
                item.SubStage,
                item.LabelId,
                item.MilestoneId,
                item.ReviewNotes,
                item.CreatedAt,
                item.CompletedAt,
                item.PipelineStage
            }).ToListAsync(ct);

        var taskComments = await db.TaskComments.AsNoTracking()
            .Where(item => taskIds.Contains(item.TaskId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.TaskId,
                item.UserId,
                item.Content,
                item.CreatedAt,
                item.UpdatedAt
            }).ToListAsync(ct);

        var taskDependencies = await db.TaskDependencies.AsNoTracking()
            .Where(item => taskIds.Contains(item.TaskId) && taskIds.Contains(item.DependentTaskId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new { item.Id, item.TaskId, item.DependentTaskId, item.CreatedAt })
            .ToListAsync(ct);

        var taskSteps = await db.TaskSteps.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId))
            .OrderBy(item => item.TaskId)
            .ThenBy(item => item.SortOrder)
            .Select(item => new
            {
                item.Id,
                item.TaskId,
                item.ProjectId,
                item.Title,
                item.SortOrder,
                item.CreatedAt,
                item.DoneAt
            }).ToListAsync(ct);

        var taskPhases = await db.TaskPhases.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId))
            .OrderBy(item => item.TaskId)
            .ThenBy(item => item.SortOrder)
            .Select(item => new
            {
                item.Id,
                item.TaskId,
                item.ProjectId,
                item.SortOrder,
                item.Key,
                item.Title,
                item.Instruction,
                item.FanOut,
                item.ModelBackendId,
                item.Status
            }).ToListAsync(ct);

        var taskWorkspaceSections = await db.TaskWorkspaceSections.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId))
            .OrderBy(item => item.TaskId)
            .ThenBy(item => item.Key)
            .Select(item => new
            {
                item.Id,
                item.TaskId,
                item.ProjectId,
                item.Key,
                item.Title,
                item.Abstract,
                item.Kind,
                item.Content,
                item.AuthorType,
                item.AuthorId,
                item.AuthorRole,
                item.AuthorProfileKey,
                item.AuthorSubtaskId,
                item.AuthorSessionId,
                item.Revision,
                item.CreatedAt,
                item.UpdatedAt
            }).ToListAsync(ct);

        var roleBindings = await db.RoleBindings.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.Role)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Role,
                item.ModelBackendId,
                item.ProfileInstructions
            }).ToListAsync(ct);

        var subtasks = await db.Subtasks.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId) && taskIds.Contains(item.TaskId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.TaskId,
                item.ProjectId,
                item.TaskPhaseId,
                item.ProfileKey,
                item.WakePlannerAfter,
                item.Instructions,
                item.Status,
                item.DiffRef,
                item.Summary,
                item.ReopenCount,
                item.CreatedAt,
                item.UpdatedAt
            }).ToListAsync(ct);

        var reports = await db.Reports.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Title,
                item.BodyMarkdown,
                item.SnapshotJson,
                item.CreatedByType,
                item.CreatedById,
                item.CreatedAt
            }).ToListAsync(ct);

        var contextSections = await db.ContextSections.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.Ordinal)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.RepoId,
                item.TaskId,
                item.SectionId,
                item.Key,
                item.Title,
                item.BodyMarkdown,
                item.Ordinal,
                item.ScopeType,
                item.Path,
                item.Source,
                item.SourcePath,
                item.ReviewState,
                item.UpdatedByType,
                item.UpdatedById,
                item.UpdatedAt
            }).ToListAsync(ct);

        var contextRevisions = await db.ContextRevisions.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.CompiledHash,
                item.CompilerVersion,
                item.TargetRepoId,
                item.TargetPath,
                item.TargetTaskId,
                item.BriefMarkdown,
                item.BriefJson,
                item.TokenEstimate,
                item.SourceNodeIds,
                item.CreatedAt
            }).ToListAsync(ct);

        var decisions = await db.Decisions.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Title,
                item.Context,
                item.DecisionBody,
                item.Consequences,
                item.Status,
                item.SupersededById,
                item.CreatedAt
            }).ToListAsync(ct);

        var decisionTasks = await db.DecisionTasks.AsNoTracking()
            .Where(item => decisionIds.Contains(item.DecisionId) && taskIds.Contains(item.TaskId))
            .Select(item => new { item.DecisionId, item.TaskId })
            .ToListAsync(ct);

        var constraints = await db.Constraints.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Body,
                item.ScopePath,
                item.Kind,
                item.Status,
                item.CreatedAt
            }).ToListAsync(ct);

        var chatSessions = await db.ChatSessions.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.Title,
                item.AgentMode,
                item.Status,
                item.CreatedAt,
                item.UpdatedAt,
                item.ArchivedAt,
                item.UsagePromptTokens,
                item.UsageCompletionTokens,
                item.UsageAssistantMessages,
                item.UsageTotalCost,
                item.UsageContextLimit,
                item.UsageGenerationSeconds
            }).ToListAsync(ct);

        var chatParts = await db.ChatParts.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.ProjectId)
            .ThenBy(item => item.SessionId)
            .ThenBy(item => item.SortOrder)
            .Select(item => new
            {
                item.Id,
                item.SessionId,
                item.ProjectId,
                item.Role,
                item.Kind,
                item.Body,
                item.ExternalId,
                item.SortOrder,
                item.CreatedAt,
                item.Tool,
                item.ToolStatus,
                item.ToolInput,
                item.ToolResult,
                item.DurationMs,
                item.Complete
            }).ToListAsync(ct);

        var attachmentRows = await db.TaskAttachments.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId))
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.ProjectId,
                item.TaskId,
                item.FileName,
                item.ContentType,
                item.Length,
                item.CreatedByType,
                item.CreatedById,
                item.CreatedAt
            }).ToListAsync(ct);

        // Billing rows are organization-owned rather than project-scoped. Export only
        // provider-neutral commercial fields; omit provider/customer/subscription ids,
        // webhook event ids and idempotency keys.
        var billingSubscriptions = await ReadBillingSubscriptionsAsync(db, orgId, ct);
        var billingInvoices = await ReadBillingInvoicesAsync(db, orgId, ct);
        var billingLedger = await ReadBillingLedgerAsync(db, orgId, ct);

        var attachmentBytes = attachmentRows.Aggregate(
            0L, (total, item) => checked(total + item.Length));
        if (attachmentBytes > MaximumAttachmentBytes)
            return TooLarge();

        var exportedAt = timeProvider.GetUtcNow();
        var jsonBudget = new ExportPayloadBudget(MaximumJsonPayloadBytes);
        await using var output = new MemoryStream();
        try
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                await AddTextEntryAsync(archive, "README.txt", BuildReadme(exportedAt), ct);
                await AddJsonEntryAsync(archive, "manifest.json", new
                {
                    schemaVersion = 1,
                    exportedAtUtc = exportedAt,
                    organizationId = orgId,
                    projectCount = projects.Count,
                    taskCount = tasks.Count,
                    attachmentCount = attachmentRows.Count,
                    billingSubscriptionCount = billingSubscriptions.Count,
                    billingInvoiceCount = billingInvoices.Count,
                    billingLedgerEntryCount = billingLedger.Count,
                    rowCount = totalRows
                }, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "organization.json", organization, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "members.json", members, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "organization-invites.json", orgInvites, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "billing-subscriptions.json", billingSubscriptions, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "billing-invoices.json", billingInvoices, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "billing-ledger.json", billingLedger, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "projects.json", projects, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "project-members.json", projectMembers, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "project-invites.json", projectInvites, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "labels.json", labels, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "label-paths.json", labelPaths, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "milestones.json", milestones, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "tasks.json", tasks, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "task-comments.json", taskComments, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "task-dependencies.json", taskDependencies, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "task-steps.json", taskSteps, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "task-phases.json", taskPhases, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "task-workspace-sections.json", taskWorkspaceSections, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "role-bindings.json", roleBindings, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "subtasks.json", subtasks, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "reports.json", reports, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "context-sections.json", contextSections, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "context-revisions.json", contextRevisions, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "decisions.json", decisions, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "decision-tasks.json", decisionTasks, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "constraints.json", constraints, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "chat-sessions.json", chatSessions, jsonBudget, ct);
                await AddJsonEntryAsync(archive, "chat-parts.json", chatParts, jsonBudget, ct);

                var attachmentManifest = new List<object>(attachmentRows.Count);
                foreach (var attachment in attachmentRows)
                {
                    ct.ThrowIfCancellationRequested();
                    // Prefer the bytea row in the same repeatable-read transaction. Only use
                    // the attachment service for legacy filesystem-backed rows, which are
                    // immutable once uploaded and still pass its safe path/content checks.
                    var storedContent = await db.TaskAttachmentContents.AsNoTracking()
                        .Where(payload => payload.ProjectId == attachment.ProjectId
                            && payload.TaskAttachmentId == attachment.Id)
                        .Select(payload => payload.Content)
                        .SingleOrDefaultAsync(ct);
                    byte[] content;
                    var detectedContentType = attachment.ContentType;
                    if (storedContent is not null)
                    {
                        content = storedContent;
                    }
                    else
                    {
                        var legacy = await attachments.ReadTaskAsync(
                            attachment.ProjectId,
                            attachment.TaskId,
                            attachment.Id,
                            ct,
                            MaximumAttachmentLength);
                        if (legacy is null)
                            return Result.Failure<OrganizationDataExport>(
                                "An attachment could not be read; no partial archive was produced.",
                                ErrorKind.Unavailable);
                        content = legacy.Value.Content;
                        detectedContentType = legacy.Value.Attachment.ContentType;
                    }

                    if (content.LongLength != attachment.Length || content.LongLength > MaximumAttachmentLength)
                        return Result.Failure<OrganizationDataExport>(
                            "An attachment failed the archive integrity or size check; no partial archive was produced.",
                            ErrorKind.Unavailable);
                    try
                    {
                        // Re-inspect bytea payloads as well as legacy files. Metadata is
                        // not a trust boundary because old rows may predate content sniffing.
                        detectedContentType = AttachmentContentInspector.DetectContentType(
                            attachment.FileName, content);
                    }
                    catch (AttachmentRejectedException)
                    {
                        return Result.Failure<OrganizationDataExport>(
                            "An attachment cannot be safely exported; no partial archive was produced.",
                            ErrorKind.Unavailable);
                    }

                    var archivePath = $"attachments/{attachment.Id:N}-{SafeFileName(attachment.FileName)}";
                    var entry = archive.CreateEntry(archivePath, CompressionLevel.Fastest);
                    await using (var entryStream = entry.Open())
                        await entryStream.WriteAsync(content.AsMemory(), ct);

                    attachmentManifest.Add(new
                    {
                        attachment.Id,
                        attachment.ProjectId,
                        attachment.TaskId,
                        attachment.FileName,
                        ContentType = detectedContentType,
                        attachment.Length,
                        attachment.CreatedByType,
                        attachment.CreatedById,
                        attachment.CreatedAt,
                        ArchivePath = archivePath
                    });
                }

                await AddJsonEntryAsync(archive, "task-attachments.json", attachmentManifest, jsonBudget, ct);
            }
        }
        catch (ExportPayloadTooLargeException)
        {
            return TooLarge();
        }

        if (output.Length > MaximumArchiveBytes)
            return TooLarge();

        // AuditEvent accepts short allowlisted codes and integer counters only.
        var details = JsonSerializer.Serialize(new
        {
            scope = "org_export",
            count = totalRows
        }, JsonOptions);
        auditWriter.Stage(db, AuditEventFactory.ForActor(
            actor,
            "organization.data_exported",
            "Org",
            orgId.ToString("D"),
            orgId,
            null,
            detailsJson: details));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var fileName = $"projectbeacon-org-{orgId:N}-export-{exportedAt:yyyyMMdd'T'HHmmss'Z'}.zip";
        return Result.Ok(new OrganizationDataExport(
            output.ToArray(),
            fileName,
            exportedAt,
            projects.Count,
            attachmentRows.Count));
    }

    private static async Task<long> CountOrganizationRowsAsync(
        BeaconDbContext db,
        string tableName,
        Guid orgId,
        CancellationToken ct)
    {
        var table = tableName switch
        {
            "OrganizationSubscriptions" => "\"OrganizationSubscriptions\"",
            "BillingInvoices" => "\"BillingInvoices\"",
            "BillingLedgerEntries" => "\"BillingLedgerEntries\"",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName))
        };

        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE \"OrgId\" = @org_id";
        AddParameter(command, "org_id", DbType.Guid, orgId);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<OrganizationSubscription>> ReadBillingSubscriptionsAsync(
        BeaconDbContext db,
        Guid orgId,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            SELECT "Id", "OrgId", "PlanKey", "Status", "Source", "TrialEndsAtUtc",
                   "CurrentPeriodStartsAtUtc", "CurrentPeriodEndsAtUtc", "CancelAtPeriodEnd",
                   "GraceUntilUtc", "CreatedAtUtc", "UpdatedAtUtc"
            FROM "OrganizationSubscriptions"
            WHERE "OrgId" = @org_id
            ORDER BY "CreatedAtUtc", "Id"
            """;
        AddParameter(command, "org_id", DbType.Guid, orgId);

        var rows = new List<OrganizationSubscription>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OrganizationSubscription(
                ReadGuid(reader, 0),
                ReadGuid(reader, 1),
                reader.GetString(2),
                (SubscriptionStatus)Convert.ToInt16(reader.GetValue(3), CultureInfo.InvariantCulture),
                (SubscriptionSource)Convert.ToInt16(reader.GetValue(4), CultureInfo.InvariantCulture),
                ReadNullableTimestamp(reader, 5),
                ReadTimestamp(reader, 6),
                ReadNullableTimestamp(reader, 7),
                Convert.ToBoolean(reader.GetValue(8), CultureInfo.InvariantCulture),
                ReadNullableTimestamp(reader, 9),
                null,
                null,
                null,
                ReadTimestamp(reader, 10),
                ReadTimestamp(reader, 11)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<BillingInvoice>> ReadBillingInvoicesAsync(
        BeaconDbContext db,
        Guid orgId,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            SELECT "Id", "OrgId", "Number", "Description", "AmountMinor", "Currency",
                   "IssuedAtUtc", "DueAtUtc", "Status", "PaidAtUtc", "PaymentReference"
            FROM "BillingInvoices"
            WHERE "OrgId" = @org_id
            ORDER BY "IssuedAtUtc", "Number"
            """;
        AddParameter(command, "org_id", DbType.Guid, orgId);

        var rows = new List<BillingInvoice>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BillingInvoice(
                ReadGuid(reader, 0),
                ReadGuid(reader, 1),
                reader.GetString(2),
                reader.GetString(3),
                Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.GetString(5),
                ReadTimestamp(reader, 6),
                ReadTimestamp(reader, 7),
                (BillingInvoiceStatus)Convert.ToInt16(reader.GetValue(8), CultureInfo.InvariantCulture),
                ReadNullableTimestamp(reader, 9),
                ReadNullableString(reader, 10),
                null,
                null));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<BillingLedgerEntry>> ReadBillingLedgerAsync(
        BeaconDbContext db,
        Guid orgId,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            SELECT "Id", "OccurredAtUtc", "OrgId", "EventType", "Source",
                   "AmountMinor", "Currency", "Reference", "ActorUserId", "Note",
                   "SubscriptionId", "InvoiceId"
            FROM "BillingLedgerEntries"
            WHERE "OrgId" = @org_id
            ORDER BY "OccurredAtUtc", "Id"
            """;
        AddParameter(command, "org_id", DbType.Guid, orgId);

        var rows = new List<BillingLedgerEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BillingLedgerEntry(
                ReadGuid(reader, 0),
                ReadTimestamp(reader, 1),
                ReadGuid(reader, 2),
                (BillingLedgerEventType)Convert.ToInt16(reader.GetValue(3), CultureInfo.InvariantCulture),
                (SubscriptionSource)Convert.ToInt16(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                ReadNullableString(reader, 6),
                null,
                null,
                ReadNullableString(reader, 7),
                ReadNullableGuid(reader, 8),
                ReadNullableString(reader, 9),
                ReadNullableGuid(reader, 10),
                null,
                ReadNullableGuid(reader, 11)));
        }

        return rows;
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) switch
        {
            Guid guidValue => guidValue,
            string stringValue => Guid.Parse(stringValue),
            byte[] bytesValue => new Guid(bytesValue),
            var otherValue => Guid.Parse(Convert.ToString(otherValue, CultureInfo.InvariantCulture)
                ?? throw new InvalidDataException("Billing row contains an invalid GUID."))
        };

    private static Guid? ReadNullableGuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(
                timestamp.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
                    : timestamp).ToUniversalTime(),
            string text => DateTimeOffset.Parse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
            _ => throw new InvalidDataException($"Unsupported billing timestamp type at column {ordinal}.")
        };
    }

    private static DateTimeOffset? ReadNullableTimestamp(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadTimestamp(reader, ordinal);

    private static Result<OrganizationDataExport> TooLarge() =>
        Result.Failure<OrganizationDataExport>(
            "This organization export exceeds the synchronous export limits. Contact support for an assisted export.",
            "export_too_large",
            ErrorKind.Conflict);

    private static async Task AddJsonEntryAsync<T>(
        ZipArchive archive,
        string name,
        T data,
        ExportPayloadBudget budget,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var entryStream = entry.Open();
        await using var boundedStream = new BudgetWriteStream(entryStream, budget, leaveOpen: true);
        await JsonSerializer.SerializeAsync(boundedStream, data, JsonOptions, ct);
    }

    private sealed class ExportPayloadBudget(long maximumBytes)
    {
        private long _remaining = maximumBytes;

        public void Consume(int bytes)
        {
            if (bytes < 0 || bytes > _remaining)
                throw new ExportPayloadTooLargeException();
            _remaining -= bytes;
        }
    }

    private sealed class ExportPayloadTooLargeException : Exception
    {
    }

    private sealed class BudgetWriteStream(
        Stream inner,
        ExportPayloadBudget budget,
        bool leaveOpen) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            budget.Consume(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            budget.Consume(buffer.Length);
            inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            budget.Consume(1);
            inner.WriteByte(value);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            budget.Consume(count);
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            budget.Consume(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!leaveOpen)
                await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    private static async Task AddTextEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes.AsMemory(), ct);
    }

    private static string SafeFileName(string fileName)
    {
        var candidate = Path.GetFileName(fileName.Replace('\\', '/'));
        var safe = new string(candidate
            .Select(character => char.IsControl(character)
                || character is '/' or '\\' or ':'
                ? '_'
                : character)
            .Take(120)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        return string.IsNullOrWhiteSpace(safe) ? "attachment.bin" : safe;
    }

    private static string BuildReadme(DateTimeOffset exportedAt) =>
        $"""
        ProjectBeacon organization data export
        Exported at (UTC): {exportedAt:O}

        This archive is a read-only snapshot. It does not delete or change source data.

        Included: organization and membership metadata, invitations (without token hashes),
        provider-neutral subscription state, manual invoices and billing ledger entries,
        projects, memberships, tasks, task phases, task workspace sections, project review
        commands and role bindings, comments, dependencies, checklists, subtasks, milestones,
        labels, reports, context, decisions, constraints, chat content, and task attachments.

        Excluded by design: password hashes, password-reset tokens, user sessions, TOTP
        secrets and recovery-code hashes, API-token hashes or raw credentials, device tokens
        and fingerprints, encrypted provider/MCP credentials, local workspace roots and
        external daemon session identifiers, provider customer/subscription references, provider
        webhook event identifiers, idempotency keys, and platform audit/security internals.
        Customer-authored text, chat messages and attachments are included as stored and may
        themselves contain sensitive values that users previously placed in that content.

        Pipeline orchestration lifecycle, review/evaluation runs, operational telemetry and platform audit/security history are not part of this first workspace export version.

        This is an application data export, not a deletion or retention request.
        """;
}
