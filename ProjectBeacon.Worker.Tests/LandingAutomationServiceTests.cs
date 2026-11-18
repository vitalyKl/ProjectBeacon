namespace ProjectBeacon.Worker.Tests;

using System.Text.Json;
using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Devices;
using ProjectBeacon.Domain.Entities.Identity;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Entities.Tasks;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Worker.Services;

public sealed class LandingAutomationServiceTests
{
    [Fact]
    public async Task DispatchCandidates_ExcludeActiveCommandsBeforeBatchingAndRecoverExpiredExecuteLeases()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<BeaconDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new BeaconDbContext(options);
        using var unscoped = TenantScope.EnterUnscoped();
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var org = Org.Create("Landing Automation");
        db.Orgs.Add(org);
        var project = Project.Create("Landing Automation", null, org.Id);
        db.Projects.Add(project);
        var owner = User.Create("landing-worker", $"landing-{Guid.NewGuid():N}@beacon.local", "hash");
        db.Users.Add(owner);
        var device = DaemonDevice.Create(
            "landing-worker",
            owner.Id,
            "landing-fp-" + Guid.NewGuid().ToString("N"),
            "landing-hash-" + Guid.NewGuid().ToString("N"),
            "bcd_" + Guid.NewGuid().ToString("N"));
        db.DaemonDevices.Add(device);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        LandingAttempt NewRevertPending()
        {
            var attempt = LandingAttempt.Start(
                project.Id, Guid.NewGuid(), Guid.NewGuid(), owner.Id, device.Id,
                "main", new string('a', 40), new string('b', 40), new string('c', 64),
                "Recover a previously requested revert.");
            attempt.RecordPreview(
                "main", new string('d', 40), new string('e', 40), new string('f', 64), 0, null);
            attempt.MarkExecutePending();
            attempt.RecordLanded(
                new string('1', 40), new string('e', 40), 0, new string('2', 64));
            attempt.MarkRevertPending();
            return attempt;
        }

        var activeAttempts = Enumerable.Range(0, 201).Select(_ => NewRevertPending()).ToArray();
        var activeCommands = activeAttempts.Select(attempt =>
            WorkstationCommand.Create(
                device.Id,
                WorkstationCommandKind.GitLand,
                JsonSerializer.Serialize(new { landingAttemptId = attempt.Id, action = "Revert" }),
                project.Id,
                owner.Id)).ToArray();

        var freeRevert = NewRevertPending();

        LandingAttempt NewExecutePending()
        {
            var attempt = LandingAttempt.Start(
                project.Id, Guid.NewGuid(), Guid.NewGuid(), owner.Id, device.Id,
                "main", new string('a', 40), new string('b', 40), new string('c', 64),
                "Recover an Execute request after process restart.");
            attempt.RecordPreview(
                "main", new string('d', 40), new string('e', 40), new string('f', 64), 0, null);
            attempt.MarkDispatchPending("Execute");
            return attempt;
        }

        var expiredExecute = NewExecutePending();
        var freshExecute = NewExecutePending();
        var completionUnknownExecute = NewExecutePending();
        completionUnknownExecute.MarkCommandCompletionUnknown();
        var activeExecute = NewExecutePending();
        var activeExecuteCommand = WorkstationCommand.Create(
            device.Id,
            WorkstationCommandKind.GitLand,
            JsonSerializer.Serialize(new { landingAttemptId = activeExecute.Id, action = "Execute" }),
            project.Id,
            owner.Id);
        var escalatedRevert = NewRevertPending();
        escalatedRevert.SetEscalationCode("ManualReviewRequired");

        db.LandingAttempts.AddRange(
            activeAttempts.Concat([
                freeRevert, expiredExecute, freshExecute, completionUnknownExecute,
                activeExecute, escalatedRevert
            ]));
        db.WorkstationCommands.AddRange(activeCommands.Append(activeExecuteCommand));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = DateTime.UtcNow;
        await db.LandingAttempts
            .Where(attempt => attempt.Id == expiredExecute.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(attempt => attempt.UpdatedAt, now.AddMinutes(-3)),
                TestContext.Current.CancellationToken);

        var candidates = await LandingAutomationService.GetDispatchCandidates(db, now)
            .Take(200)
            .ToListAsync(TestContext.Current.CancellationToken);
        var candidateIds = candidates.Select(attempt => attempt.Id).ToHashSet();

        Assert.Equal(3, candidates.Count);
        Assert.Contains(freeRevert.Id, candidateIds);
        Assert.Contains(expiredExecute.Id, candidateIds);
        Assert.Contains(completionUnknownExecute.Id, candidateIds);
        Assert.DoesNotContain(freshExecute.Id, candidateIds);
        Assert.DoesNotContain(activeExecute.Id, candidateIds);
        Assert.DoesNotContain(escalatedRevert.Id, candidateIds);
        Assert.DoesNotContain(activeAttempts.Select(attempt => attempt.Id).First(), candidateIds);
    }

    [Theory]
    [InlineData(LandingAttemptStatus.Previewed, 0, false)]
    [InlineData(LandingAttemptStatus.Previewed, 1, false)]
    [InlineData(LandingAttemptStatus.Previewed, 2, true)]
    [InlineData(LandingAttemptStatus.ExecutePending, 0, false)]
    [InlineData(LandingAttemptStatus.ExecutePending, 1, false)]
    [InlineData(LandingAttemptStatus.ExecutePending, 2, true)]
    [InlineData(LandingAttemptStatus.Landed, 2, false)]
    [InlineData(LandingAttemptStatus.RevertPending, 2, false)]
    public void ShouldDispatchExecute_RecoversOnlyApprovedAutomationLevels(
        LandingAttemptStatus status,
        int autonomyLevel,
        bool expected)
    {
        Assert.Equal(expected, LandingAutomationService.ShouldDispatchExecute(status, autonomyLevel));
    }

    [Theory]
    [InlineData(LandingAttemptStatus.RevertPending, "Off", null, true)]
    [InlineData(LandingAttemptStatus.RevertPending, "OnPostCheckFail", 1, true)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "OnPostCheckFail", 1, true)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "Off", 1, false)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "OnPostCheckFail", 0, false)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "OnPostCheckFail", -1, false)]
    [InlineData(LandingAttemptStatus.LandedUnverified, "OnPostCheckFail", null, false)]
    public void ShouldDispatchRevert_SeparatesNewAutomaticRevertsFromPendingRecovery(
        LandingAttemptStatus status,
        string mode,
        int? checkExitCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            LandingAutomationService.ShouldDispatchRevert(status, mode, checkExitCode));
    }
}
