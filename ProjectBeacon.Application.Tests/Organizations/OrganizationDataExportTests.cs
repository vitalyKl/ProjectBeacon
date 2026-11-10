namespace ProjectBeacon.Application.Tests.Organizations;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ProjectBeacon.Application.Authorization;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Entities.Tasks;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Attachments;
using ProjectBeacon.Infrastructure.Audit;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Infrastructure.Organizations;
using Microsoft.EntityFrameworkCore;

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
        project.SetMcpServersCipher("encrypted-mcp-credential-must-not-export");
        var task = TaskItem.Create("Export task", project.Id);
        task.Update(description: "Task description");
        task.SetReviewNotes("Review notes belong in the archive.");
        _db.Users.Add(owner);
        _db.Orgs.Add(org);
        _db.Projects.Add(project);
        _db.OrgMembers.Add(OrgMember.Create(org.Id, owner.Id, MemberRole.Owner));
        _db.Tasks.Add(task);
        _db.TaskComments.Add(TaskComment.Create("Task comment", task.Id, owner.Id));

        var orgInvite = OrgInvite.Create(
            org.Id, "invited@example.test", MemberRole.Member, owner.Id, "org-invite-token-hash");
        var projectInvite = ProjectInvite.Create(
            project.Id, "project-invite@example.test", MemberRole.Member, owner.Id, "project-invite-token-hash");
        _db.OrgInvites.Add(orgInvite);
        _db.ProjectInvites.Add(projectInvite);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        Assert.Equal(1, result.Value.ProjectCount);
        Assert.Equal(1, result.Value.AttachmentCount);
        Assert.StartsWith($"projectbeacon-org-{org.Id:N}-export-", result.Value.FileName, StringComparison.Ordinal);

        using var archiveStream = new MemoryStream(result.Value.Content);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("organization.json", names);
        Assert.Contains("members.json", names);
        Assert.Contains("organization-invites.json", names);
        Assert.Contains("projects.json", names);
        Assert.Contains("tasks.json", names);
        Assert.Contains("task-comments.json", names);
        Assert.Contains("task-attachments.json", names);
        Assert.Contains("README.txt", names);

        var projectsJson = await ReadEntryAsync(archive, "projects.json");
        Assert.Contains("Export Project", projectsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted-mcp-credential-must-not-export", projectsJson, StringComparison.Ordinal);

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
