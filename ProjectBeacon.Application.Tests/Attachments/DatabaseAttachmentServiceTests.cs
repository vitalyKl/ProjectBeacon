namespace ProjectBeacon.Application.Tests;

using System.Text;
using Application.Authorization;
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
        var org = Org.Create("Attachments");
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var project = Project.Create("Attachments", null, org.Id);
        _db.Projects.Add(project);
        var task = TaskItem.Create("Attachment task", project.Id);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new DatabaseAttachmentService(HandlerSqlite.Factory(_connection));
        var bytes = Encoding.UTF8.GetBytes("attachment payload");
        var actor = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, project.Id, org.Id, ApiTokenCapability.None);
        await using var source = new MemoryStream(bytes);

        var uploaded = await service.UploadTaskAsync(
            project.Id, task.Id, actor, source, "notes.txt", "text/plain", bytes.Length,
            TestContext.Current.CancellationToken);

        Assert.NotNull(uploaded);
        Assert.Equal(bytes.Length, uploaded.Length);

        var payload = await _db.TaskAttachmentContents.AsNoTracking()
            .SingleAsync(item => item.TaskAttachmentId == uploaded.Id, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, payload.Content);

        var listed = await service.ListTaskAsync(project.Id, task.Id, TestContext.Current.CancellationToken);
        var listedAttachment = Assert.Single(listed);
        Assert.Equal(uploaded.Id, listedAttachment.Id);
        Assert.Equal("notes.txt", listedAttachment.FileName);

        var downloaded = await service.ReadTaskAsync(
            project.Id, task.Id, uploaded.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(downloaded);
        Assert.Equal(bytes, downloaded.Value.Content);
    }

    [Fact]
    public async Task UploadRejectsLengthMismatch_WithoutCreatingMetadataOrPayload()
    {
        var org = Org.Create("Attachments");
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var project = Project.Create("Attachments", null, org.Id);
        _db.Projects.Add(project);
        var task = TaskItem.Create("Attachment task", project.Id);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new DatabaseAttachmentService(HandlerSqlite.Factory(_connection));
        var actor = new ActorContext(
            ActorType.Human, Guid.NewGuid(), false, null, null, project.Id, org.Id, ApiTokenCapability.None);
        await using var source = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.UploadTaskAsync(
            project.Id, task.Id, actor, source, "notes.bin", "application/octet-stream", 4,
            TestContext.Current.CancellationToken));

        Assert.Empty(await _db.TaskAttachments.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await _db.TaskAttachmentContents.ToListAsync(TestContext.Current.CancellationToken));
    }
}
