namespace ProjectBeacon.Application.Tests.Organizations;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Application.Tests;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Entities.Tasks;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Attachments;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using ProjectBeacon.Application.Common;

public sealed class OrganizationDataExportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;
    private bool _scopeDisposed;

    public OrganizationDataExportTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
    }

    public void Dispose()
    {
        if (!_scopeDisposed)
            _unscoped.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Export_IncludesOrganizationWorkspaceAndAttachmentButExcludesCredentials()
    {
        var owner = User.Create("owner", "owner@example.test", "password-hash-must-not-export");
        var org = Org.Create("Export Org", "Customer-owned description");
        var project = Project.Create("Export Project", "Project description", org.Id);
        project.Update(reviewCheckCommand: "dotnet test");
        var foreignOwner = User.Create("foreign-owner", "foreign-owner@example.test", "foreign-password-hash");
        var foreignOrg = Org.Create("Foreign Org");
        var foreignProject = Project.Create("FOREIGN PROJECT MUST NOT EXPORT", null, foreignOrg.Id);
        var foreignTask = TaskItem.Create("FOREIGN TASK MUST NOT EXPORT", foreignProject.Id);
        project.SetMcpServersCipher("encrypted-mcp-credential-must-not-export");
        var task = TaskItem.Create("Export task", project.Id);
        task.Update(description: "Task description");
        task.SetReviewNotes("Review notes belong in the archive.");
        var phaseTemplate = TaskKindPhase.Create(
            Guid.NewGuid(), 0, "plan", "Planning", "Plan the task before execution.", false, null);
        var phase = TaskPhase.CopyFrom(phaseTemplate, task.Id, project.Id, null);
        var workspaceSection = TaskWorkspaceSection.Create(
            task.Id, project.Id, "implementation-notes", "Implementation notes",
            "A concise summary", "Notes", "Customer-owned workspace content.",
            "Human", owner.Id.ToString("D"));
        _db.Users.AddRange(owner, foreignOwner);
        _db.Orgs.AddRange(org, foreignOrg);
        _db.Projects.AddRange(project, foreignProject);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, owner.Id, MemberRole.Owner));
        _db.OrgMembers.Add(OrgMember.Create(foreignOrg.Id, foreignOwner.Id, MemberRole.Owner));
        _db.Tasks.AddRange(task, foreignTask);
        _db.TaskPhases.Add(phase);
        _db.TaskWorkspaceSections.Add(workspaceSection);
        _db.TaskComments.Add(TaskComment.Create("Task comment", task.Id, owner.Id));

        var orgInvite = OrgInvite.Create(
            org.Id, "invited@example.test", MemberRole.Member, owner.Id, "org-invite-token-hash");
        var projectInvite = ProjectInvite.Create(
            project.Id, "project-invite@example.test", MemberRole.Member, owner.Id, "project-invite-token-hash");
        _db.OrgInvites.Add(orgInvite);
        _db.ProjectInvites.Add(projectInvite);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var subscriptionId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var ledgerId = Guid.NewGuid();
        var billingTime = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await _db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "OrganizationSubscriptions" (
                "Id" TEXT NOT NULL, "OrgId" TEXT NOT NULL, "PlanKey" TEXT NOT NULL,
                "Status" INTEGER NOT NULL, "Source" INTEGER NOT NULL,
                "TrialEndsAtUtc" TEXT NULL, "CurrentPeriodStartsAtUtc" TEXT NOT NULL,
                "CurrentPeriodEndsAtUtc" TEXT NULL, "CancelAtPeriodEnd" INTEGER NOT NULL,
                "GraceUntilUtc" TEXT NULL, "ProviderKey" TEXT NULL,
                "ExternalCustomerReference" TEXT NULL, "ExternalSubscriptionReference" TEXT NULL,
                "CreatedAtUtc" TEXT NOT NULL, "UpdatedAtUtc" TEXT NOT NULL
            );
            CREATE TABLE "BillingInvoices" (
                "Id" TEXT NOT NULL, "OrgId" TEXT NOT NULL, "Number" TEXT NOT NULL,
                "Description" TEXT NOT NULL, "AmountMinor" INTEGER NOT NULL, "Currency" TEXT NOT NULL,
                "IssuedAtUtc" TEXT NOT NULL, "DueAtUtc" TEXT NOT NULL, "Status" INTEGER NOT NULL,
                "PaidAtUtc" TEXT NULL, "PaymentReference" TEXT NULL,
                "ProviderKey" TEXT NULL, "ExternalInvoiceReference" TEXT NULL
            );
            CREATE TABLE "BillingLedgerEntries" (
                "Id" TEXT NOT NULL, "OccurredAtUtc" TEXT NOT NULL, "OrgId" TEXT NOT NULL,
                "EventType" INTEGER NOT NULL, "Source" INTEGER NOT NULL, "AmountMinor" INTEGER NULL,
                "Currency" TEXT NULL, "ProviderKey" TEXT NULL, "ProviderEventId" TEXT NULL,
                "Reference" TEXT NULL, "ActorUserId" TEXT NULL, "Note" TEXT NULL,
                "IdempotencyKey" TEXT NULL, "SubscriptionId" TEXT NULL, "InvoiceId" TEXT NULL
            );
            """,
            TestContext.Current.CancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "OrganizationSubscriptions"
                ("Id", "OrgId", "PlanKey", "Status", "Source", "CurrentPeriodStartsAtUtc",
                 "CancelAtPeriodEnd", "ProviderKey", "ExternalCustomerReference",
                 "ExternalSubscriptionReference", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES
                ({subscriptionId.ToString("D")}, {org.Id.ToString("D")}, {"alpha"}, {1}, {0},
                 {billingTime}, {false}, {"provider-secret-key"}, {"provider-customer-reference"},
                 {"provider-subscription-reference"}, {billingTime}, {billingTime});
            """,
            TestContext.Current.CancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "BillingInvoices"
                ("Id", "OrgId", "Number", "Description", "AmountMinor", "Currency",
                 "IssuedAtUtc", "DueAtUtc", "Status", "PaidAtUtc", "PaymentReference",
                 "ProviderKey", "ExternalInvoiceReference")
            VALUES
                ({invoiceId.ToString("D")}, {org.Id.ToString("D")}, {"PB-2026-000001"},
                 {"Manual alpha invoice"}, {1999L}, {"EUR"}, {billingTime}, {billingTime},
                 {2}, {billingTime}, {"receipt-reference-123"}, {"provider-secret-key"},
                 {"provider-invoice-reference"});
            """,
            TestContext.Current.CancellationToken);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "BillingLedgerEntries"
                ("Id", "OccurredAtUtc", "OrgId", "EventType", "Source", "AmountMinor",
                 "Currency", "ProviderKey", "ProviderEventId", "Reference", "ActorUserId",
                 "Note", "IdempotencyKey", "SubscriptionId", "InvoiceId")
            VALUES
                ({ledgerId.ToString("D")}, {billingTime}, {org.Id.ToString("D")}, {8}, {0},
                 {1999L}, {"EUR"}, {"provider-secret-key"}, {"provider-webhook-event-id"},
                 {"receipt-reference-123"}, {owner.Id.ToString("D")}, {"Marked paid manually"},
                 {"idempotency-secret-key"}, {subscriptionId.ToString("D")}, {invoiceId.ToString("D")});
            """,
            TestContext.Current.CancellationToken);

        var factory = HandlerSqlite.Factory(_connection);
        var attachmentService = new DatabaseAttachmentService(factory);
        var actor = Human(owner.Id);
        var bytes = Encoding.UTF8.GetBytes("customer attachment payload");
        using (var input = new MemoryStream(bytes))
        using (TenantScope.EnterProjectScope(project.Id))
        {
            var uploaded = await attachmentService.UploadTaskAsync(
                project.Id,
                task.Id,
                actor,
                input,
                "notes.md",
                "text/markdown",
                bytes.LongLength,
                TestContext.Current.CancellationToken);
            Assert.NotNull(uploaded);
        }

        // Leave seed/bootstrap scope so the exporter must bind the requested organization itself.
        _unscoped.Dispose();
        _scopeDisposed = true;

        var exporter = new EfOrganizationDataExportService(
            factory,
            new AuthorizationService(),
            new EfAuditWriter(),
            attachmentService,
            TimeProvider.System);

        var result = await exporter.ExportAsync(
            org.Id, actor, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Value);
        var exported = result.Value!;
        Assert.Equal(1, exported.ProjectCount);
        Assert.Equal(1, exported.AttachmentCount);
        Assert.StartsWith($"projectbeacon-org-{org.Id:N}-export-", exported.FileName);

        using var archiveStream = new MemoryStream(exported.Content);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("manifest.json", names);
        Assert.Contains("organization.json", names);
        Assert.Contains("members.json", names);
        Assert.Contains("organization-invites.json", names);
        Assert.Contains("billing-subscriptions.json", names);
        Assert.Contains("billing-invoices.json", names);
        Assert.Contains("billing-ledger.json", names);
        Assert.Contains("projects.json", names);
        Assert.Contains("tasks.json", names);
        Assert.Contains("task-phases.json", names);
        Assert.Contains("task-workspace-sections.json", names);
        Assert.Contains("role-bindings.json", names);
        Assert.Contains("task-comments.json", names);
        Assert.Contains("task-attachments.json", names);
        Assert.Contains("README.txt", names);

        var projectsJson = await ReadEntryAsync(archive, "projects.json");
        Assert.Contains("Export Project", projectsJson, StringComparison.Ordinal);
        Assert.Contains("dotnet test", projectsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN PROJECT MUST NOT EXPORT", projectsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted-mcp-credential-must-not-export", projectsJson, StringComparison.Ordinal);

        var taskJson = await ReadEntryAsync(archive, "tasks.json");
        Assert.Contains("Export task", taskJson, StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN TASK MUST NOT EXPORT", taskJson, StringComparison.Ordinal);

        var taskPhasesJson = await ReadEntryAsync(archive, "task-phases.json");
        Assert.Contains("Plan the task before execution.", taskPhasesJson, StringComparison.Ordinal);
        var workspaceJson = await ReadEntryAsync(archive, "task-workspace-sections.json");
        Assert.Contains("Customer-owned workspace content.", workspaceJson, StringComparison.Ordinal);

        var manifestJson = await ReadEntryAsync(archive, "manifest.json");
        using (var manifestDoc = JsonDocument.Parse(manifestJson))
        {
            Assert.Equal(1, manifestDoc.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1, manifestDoc.RootElement.GetProperty("billingSubscriptionCount").GetInt32());
            Assert.Equal(1, manifestDoc.RootElement.GetProperty("billingInvoiceCount").GetInt32());
            Assert.Equal(1, manifestDoc.RootElement.GetProperty("billingLedgerEntryCount").GetInt32());
        }

        var subscriptionJson = await ReadEntryAsync(archive, "billing-subscriptions.json");
        Assert.Contains("alpha", subscriptionJson, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret-key", subscriptionJson, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-customer-reference", subscriptionJson, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-subscription-reference", subscriptionJson, StringComparison.Ordinal);
        var invoiceJson = await ReadEntryAsync(archive, "billing-invoices.json");
        Assert.Contains("PB-2026-000001", invoiceJson, StringComparison.Ordinal);
        Assert.Contains("receipt-reference-123", invoiceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret-key", invoiceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-invoice-reference", invoiceJson, StringComparison.Ordinal);
        var ledgerJson = await ReadEntryAsync(archive, "billing-ledger.json");
        Assert.Contains("Marked paid manually", ledgerJson, StringComparison.Ordinal);
        Assert.Contains("receipt-reference-123", ledgerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret-key", ledgerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-webhook-event-id", ledgerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("idempotency-secret-key", ledgerJson, StringComparison.Ordinal);

        var memberJson = await ReadEntryAsync(archive, "members.json");
        Assert.Contains("owner@example.test", memberJson, StringComparison.Ordinal);
        Assert.DoesNotContain("password-hash-must-not-export", memberJson, StringComparison.Ordinal);

        var orgInviteJson = await ReadEntryAsync(archive, "organization-invites.json");
        Assert.Contains("invited@example.test", orgInviteJson, StringComparison.Ordinal);
        Assert.DoesNotContain("org-invite-token-hash", orgInviteJson, StringComparison.Ordinal);
        Assert.DoesNotContain("TokenHash", orgInviteJson, StringComparison.Ordinal);

        var projectInviteJson = await ReadEntryAsync(archive, "project-invites.json");
        Assert.Contains("project-invite@example.test", projectInviteJson, StringComparison.Ordinal);
        Assert.DoesNotContain("project-invite-token-hash", projectInviteJson, StringComparison.Ordinal);

        var attachmentManifest = await ReadEntryAsync(archive, "task-attachments.json");
        using var manifest = JsonDocument.Parse(attachmentManifest);
        var archivePath = manifest.RootElement[0].GetProperty("archivePath").GetString();
        Assert.False(string.IsNullOrWhiteSpace(archivePath));
        var contentEntry = archive.GetEntry(archivePath!);
        Assert.NotNull(contentEntry);
        await using var contentStream = contentEntry!.Open();
        using var contentBuffer = new MemoryStream();
        await contentStream.CopyToAsync(contentBuffer, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, contentBuffer.ToArray());

        var readme = await ReadEntryAsync(archive, "README.txt");
        Assert.Contains("TOTP", readme, StringComparison.Ordinal);
        Assert.Contains("API-token", readme, StringComparison.Ordinal);
        Assert.Contains("does not delete", readme, StringComparison.OrdinalIgnoreCase);

        // The export itself is recorded against the owning organization.
        Assert.True(await _db.AuditEvents.IgnoreQueryFilters().AnyAsync(
            audit => audit.OrgId == org.Id && audit.Action == "organization.data_exported",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Export_RejectsHumanWithoutOrganizationAdminMembership()
    {
        var owner = User.Create("owner", "owner@denied.test", "hash");
        var other = User.Create("other", "other@denied.test", "hash");
        var org = Org.Create("Private org");
        _db.Users.AddRange(owner, other);
        _db.Orgs.Add(org);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, owner.Id, MemberRole.Owner));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _unscoped.Dispose();
        _scopeDisposed = true;

        var factory = HandlerSqlite.Factory(_connection);
        var exporter = new EfOrganizationDataExportService(
            factory,
            new AuthorizationService(),
            new EfAuditWriter(),
            new DatabaseAttachmentService(factory),
            TimeProvider.System);

        var denied = await exporter.ExportAsync(
            org.Id, Human(other.Id), TestContext.Current.CancellationToken);

        Assert.False(denied.Success);
        Assert.Equal(ErrorKind.Forbidden, denied.Kind);
        Assert.Null(denied.Value);
        Assert.False(await _db.AuditEvents.IgnoreQueryFilters().AnyAsync(
            audit => audit.OrgId == org.Id && audit.Action == "organization.data_exported",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Export_DoesNotUsePlatformAdministratorMembershipBypass()
    {
        var platformAdmin = User.Create(
            "platform-admin", "platform-admin@example.test", "hash", isAdmin: true);
        var org = Org.Create("Customer org without operator membership");
        _db.Users.Add(platformAdmin);
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _unscoped.Dispose();
        _scopeDisposed = true;

        var factory = HandlerSqlite.Factory(_connection);
        var exporter = new EfOrganizationDataExportService(
            factory,
            new AuthorizationService(),
            new EfAuditWriter(),
            new DatabaseAttachmentService(factory),
            TimeProvider.System);

        var denied = await exporter.ExportAsync(
            org.Id,
            new ActorContext(
                ActorType.Human,
                platformAdmin.Id,
                IsAdmin: true,
                null,
                null,
                null,
                null,
                ApiTokenCapability.None),
            TestContext.Current.CancellationToken);

        Assert.False(denied.Success);
        Assert.Equal(ErrorKind.Forbidden, denied.Kind);
        Assert.Null(denied.Value);
    }

    private static ActorContext Human(Guid userId) =>
        new(ActorType.Human, userId, false, null, null, null, null, ApiTokenCapability.None);

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        await using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
