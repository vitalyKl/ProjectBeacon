namespace ProjectBeacon.Application.Tests;

using System.Text;
using Application.Authorization;
using Application.Persistence;
using Domain.Entities.Identity;
using Domain.Entities.Projects;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Attachments;
using Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class DatabaseAttachmentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BeaconDbContext _db;
    private readonly IDisposable _unscoped;

    public DatabaseAttachmentServiceTests()
    {
        (_connection, _db, _unscoped) = HandlerSqlite.Open();
    }

    public void Dispose()
    {
        _db.Dispose();
        _unscoped.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task UploadStoresBytesInDatabase_AndListAndReadReturnExpectedData()
    {
        var fixture = await CreateTaskAsync();
        var service = new DatabaseAttachmentService(fixture.Factory);
        var bytes = Encoding.UTF8.GetBytes("attachment payload");
        await using var source = new MemoryStream(bytes);

        var uploaded = await service.UploadTaskAsync(
            fixture.Project.Id, fixture.Task.Id, fixture.Actor, source,
            "../notes.txt", "image/png", bytes.Length, TestContext.Current.CancellationToken);

        Assert.NotNull(uploaded);
        Assert.Equal(bytes.Length, uploaded.Length);
        Assert.Equal("text/plain; charset=utf-8", uploaded.ContentType);

        var payload = await _db.TaskAttachmentContents.AsNoTracking()
            .SingleAsync(item => item.TaskAttachmentId == uploaded.Id, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, payload.Content);

        var listed = await service.ListTaskAsync(
            fixture.Project.Id, fixture.Task.Id, TestContext.Current.CancellationToken);
        var listedAttachment = Assert.Single(listed);
        Assert.Equal(uploaded.Id, listedAttachment.Id);
        Assert.Equal("notes.txt", listedAttachment.FileName);

        var downloaded = await service.ReadTaskAsync(
            fixture.Project.Id, fixture.Task.Id, uploaded.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(downloaded);
        Assert.Equal(bytes, downloaded.Value.Content);
        Assert.Equal("text/plain; charset=utf-8", downloaded.Value.Attachment.ContentType);
    }

    [Fact]
    public async Task UploadRejectsLengthMismatch_WithoutCreatingMetadataOrPayload()
    {
        var fixture = await CreateTaskAsync();
        var service = new DatabaseAttachmentService(fixture.Factory);
        await using var source = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<AttachmentRejectedException>(() => service.UploadTaskAsync(
            fixture.Project.Id, fixture.Task.Id, fixture.Actor, source,
            "notes.txt", "text/plain", 4, TestContext.Current.CancellationToken));

        Assert.Empty(await _db.TaskAttachments.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await _db.TaskAttachmentContents.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UploadDetectsImageFromSignature_NotClientContentType()
    {
        var fixture = await CreateTaskAsync();
        var service = new DatabaseAttachmentService(fixture.Factory);
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aN8AAAAASUVORK5CYII=");
        await using var source = new MemoryStream(bytes);

        var uploaded = await service.UploadTaskAsync(
            fixture.Project.Id, fixture.Task.Id, fixture.Actor, source,
            "pixel.png", "text/html", bytes.Length, TestContext.Current.CancellationToken);

        Assert.NotNull(uploaded);
        Assert.Equal("image/png", uploaded.ContentType);
    }

    [Fact]
    public async Task UploadRejectsExecutableAndArchiveSignatures()
    {
        var fixture = await CreateTaskAsync();
        var service = new DatabaseAttachmentService(fixture.Factory);
        var executable = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 };
        var archive = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 };

        await using var executableStream = new MemoryStream(executable);
        await Assert.ThrowsAsync<AttachmentRejectedException>(() => service.UploadTaskAsync(
            fixture.Project.Id, fixture.Task.Id, fixture.Actor, executableStream,
            "payload.dat", "application/octet-stream", executable.Length, TestContext.Current.CancellationToken));

        await using var archiveStream = new MemoryStream(archive);
        await Assert.ThrowsAsync<AttachmentRejectedException>(() => service.UploadTaskAsync(
            fixture.Project.Id, fixture.Task.Id, fixture.Actor, archiveStream,
            "payload.dat", "application/octet-stream", archive.Length, TestContext.Current.CancellationToken));

        Assert.Empty(await _db.TaskAttachments.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await _db.TaskAttachmentContents.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UploadEnforcesProjectQuotaAndPerTaskAttachmentCount()
    {
        var fixture = await CreateTaskAsync();
        var projectQuotaService = new DatabaseAttachmentService(fixture.Factory, 3, 10);
        var bytes = Encoding.UTF8.GetBytes("abc");

        await using (var first = new MemoryStream(bytes))
        {
            var uploaded = await projectQuotaService.UploadTaskAsync(
                fixture.Project.Id, fixture.Task.Id, fixture.Actor, first,
                "first.txt", "text/plain", bytes.Length, TestContext.Current.CancellationToken);
            Assert.NotNull(uploaded);
        }

        await using (var overQuota = new MemoryStream([1]))
        {
            await Assert.ThrowsAsync<AttachmentQuotaExceededException>(() => projectQuotaService.UploadTaskAsync(
                fixture.Project.Id, fixture.Task.Id, fixture.Actor, overQuota,
                "second.bin", "application/octet-stream", 1, TestContext.Current.CancellationToken));
        }

        var taskCountService = new DatabaseAttachmentService(fixture.Factory, 100, 1);
        await using (var overCount = new MemoryStream([2]))
        {
            await Assert.ThrowsAsync<AttachmentQuotaExceededException>(() => taskCountService.UploadTaskAsync(
                fixture.Project.Id, fixture.Task.Id, fixture.Actor, overCount,
                "third.bin", "application/octet-stream", 1, TestContext.Current.CancellationToken));
        }

        Assert.Single(await _db.TaskAttachments.ToListAsync(TestContext.Current.CancellationToken));
    }

    private async Task<AttachmentFixture> CreateTaskAsync()
    {
        var org = Org.Create("Attachments");
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var project = Project.Create("Attachments", null, org.Id);
        _db.Projects.Add(project);
        var task = TaskItem.Create("Attachment task", project.Id);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actor = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, project.Id, org.Id, ApiTokenCapability.None);
        return new AttachmentFixture(project, task, actor, HandlerSqlite.Factory(_connection));
    }

    private sealed record AttachmentFixture(
        Project Project, TaskItem Task, ActorContext Actor, IBeaconDbFactory Factory);
}
