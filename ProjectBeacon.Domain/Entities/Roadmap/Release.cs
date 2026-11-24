namespace ProjectBeacon.Domain.Entities.Roadmap;

using ProjectBeacon.Domain.Common;
using ProjectBeacon.Domain.Entities.Projects;
using ProjectBeacon.Domain.Enums;

/// <summary>
/// A project release groups milestones and selects the intended branch/version target.
/// It is independent of Milestone; publishing is a separate server-authorized operation.
/// </summary>
public class Release : Entity, IProjectScoped
{
    public Release() { }

    public int Revision { get; private set; } = 1;
    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? TargetVersion { get; private set; }
    public string TargetBranch { get; private set; } = string.Empty;
    public ReleaseStatus Status { get; private set; } = ReleaseStatus.Planned;
    public int Order { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public DateTime? PublishedAt { get; private set; }

    /// <summary>Server-owned revision for Prepare/VerifyLanding command transitions.</summary>
    public int PreparationRevision { get; private set; } = 1;
    public ReleasePreparationStatus PreparationStatus { get; private set; } = ReleasePreparationStatus.NotStarted;
    /// <summary>The first workstation selected for this release remains pinned for every retry and verification.</summary>
    public Guid? PreparationDeviceId { get; private set; }
    public Guid? CandidateId { get; private set; }
    public Guid? PreparationCommandId { get; private set; }
    public Guid? VerificationCommandId { get; private set; }
    /// <summary>Immutable Prepare request. Retries must match it byte-for-byte.</summary>
    public string? PreparationPayloadJson { get; private set; }
    /// <summary>Validated Prepare result containing the PR and prepared commit evidence.</summary>
    public string? PreparationResultJson { get; private set; }
    public string? PreparationError { get; private set; }
    public DateTime? PreparationUpdatedAtUtc { get; private set; }

    public Project Project { get; private set; } = null!;
    public ICollection<Milestone> Milestones { get; private set; } = [];
    public ReleaseCandidateSnapshot? CandidateSnapshot { get; private set; }

    public static Release Create(
        string name,
        string? description,
        Guid projectId,
        string? targetVersion,
        string targetBranch,
        int order)
    {
        var release = Entity.New<Release>();
        release.Name = name;
        release.Description = description;
        release.ProjectId = projectId;
        release.TargetVersion = targetVersion;
        release.TargetBranch = targetBranch;
        release.Order = order;
        release.Status = ReleaseStatus.Planned;
        release.CreatedAt = DateTime.UtcNow;
        return release;
    }

    public void Update(
        string? name = null,
        string? description = null,
        string? targetVersion = null,
        string? targetBranch = null,
        int? order = null)
    {
        if (Status is ReleaseStatus.Candidate or ReleaseStatus.Published or ReleaseStatus.Cancelled
            || PreparationStatus != ReleasePreparationStatus.NotStarted)
            throw new InvalidOperationException("A release cannot be edited after ReleasePrepare has been started.");

        var changed = false;
        if (name is not null && !string.Equals(Name, name, StringComparison.Ordinal))
        {
            Name = name;
            changed = true;
        }
        if (description is not null)
        {
            var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            if (!string.Equals(Description, normalizedDescription, StringComparison.Ordinal))
            {
                Description = normalizedDescription;
                changed = true;
            }
        }
        if (targetVersion is not null)
        {
            var normalizedVersion = string.IsNullOrWhiteSpace(targetVersion) ? null : targetVersion.Trim();
            if (!string.Equals(TargetVersion, normalizedVersion, StringComparison.Ordinal))
            {
                TargetVersion = normalizedVersion;
                changed = true;
            }
        }
        if (targetBranch is not null && !string.Equals(TargetBranch, targetBranch, StringComparison.Ordinal))
        {
            TargetBranch = targetBranch;
            changed = true;
        }
        if (order is not null && Order != order.Value)
        {
            Order = order.Value;
            changed = true;
        }
        if (!changed)
            return;

        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Changes planning status. Published can only be set by the publication workflow.</summary>
    public void TransitionTo(ReleaseStatus target)
    {
        if (target == Status)
            return;

        if (target == ReleaseStatus.Candidate)
            throw new InvalidOperationException("Only the authorized ReleasePrepare workflow can create an immutable Candidate.");
        if (target == ReleaseStatus.Cancelled
            && PreparationStatus is not (ReleasePreparationStatus.NotStarted or ReleasePreparationStatus.Failed))
            throw new InvalidOperationException("A release cannot be cancelled while preparation or verification evidence is active.");

        var allowed = Status switch
        {
            ReleaseStatus.Planned => target is ReleaseStatus.InProgress or ReleaseStatus.Cancelled,
            ReleaseStatus.InProgress => target == ReleaseStatus.Cancelled,
            ReleaseStatus.Candidate => target == ReleaseStatus.Cancelled,
            ReleaseStatus.Published or ReleaseStatus.Cancelled => false,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Release cannot transition from {Status} to {target}.");

        if (target == ReleaseStatus.Published)
            throw new InvalidOperationException("Only the authorized publication workflow can publish a release.");

        Status = target;
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Starts a trusted Prepare command and pins the exact workstation and immutable request.</summary>
    public void BeginPreparation(Guid deviceId, Guid candidateId, Guid commandId, string payloadJson)
    {
        if (Status != ReleaseStatus.InProgress)
            throw new InvalidOperationException("Only an InProgress release can be prepared.");
        if (deviceId == Guid.Empty || candidateId == Guid.Empty || commandId == Guid.Empty)
            throw new ArgumentException("Preparation device, candidate and command ids are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (payloadJson.Length > 1_000_000)
            throw new ArgumentException("ReleasePrepare payload exceeds the size limit.", nameof(payloadJson));
        if (PreparationStatus is not (ReleasePreparationStatus.NotStarted or ReleasePreparationStatus.Failed))
            throw new InvalidOperationException("ReleasePrepare is already queued or has advanced beyond Prepare.");
        if (PreparationDeviceId is { } pinnedDevice && pinnedDevice != deviceId)
            throw new InvalidOperationException("This Release is pinned to another workstation; automatic workstation switching is forbidden.");
        if (CandidateId is { } pinnedCandidate && pinnedCandidate != candidateId)
            throw new InvalidOperationException("A retry must use the original Candidate id.");
        if (PreparationPayloadJson is not null
            && !string.Equals(PreparationPayloadJson, payloadJson, StringComparison.Ordinal))
            throw new InvalidOperationException("A retry must use the original immutable ReleasePrepare payload.");

        PreparationDeviceId = deviceId;
        CandidateId = candidateId;
        PreparationCommandId = commandId;
        VerificationCommandId = null;
        PreparationPayloadJson = payloadJson;
        PreparationResultJson = null;
        PreparationError = null;
        PreparationStatus = ReleasePreparationStatus.PrepareQueued;
        PreparationRevision++;
        PreparationUpdatedAtUtc = DateTime.UtcNow;
        UpdatedAt = PreparationUpdatedAtUtc;
    }

    public void RecordPrepareSuccess(Guid commandId, string resultJson)
    {
        RequirePreparationCommand(commandId, ReleasePreparationStatus.PrepareQueued, PreparationCommandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        if (resultJson.Length > 1_000_000)
            throw new ArgumentException("ReleasePrepare result exceeds the size limit.", nameof(resultJson));
        PreparationResultJson = resultJson;
        PreparationError = null;
        PreparationStatus = ReleasePreparationStatus.Prepared;
        PreparationRevision++;
        PreparationUpdatedAtUtc = DateTime.UtcNow;
        UpdatedAt = PreparationUpdatedAtUtc;
    }

    public void RecordPrepareFailure(Guid commandId, string error)
    {
        RequirePreparationCommand(commandId, ReleasePreparationStatus.PrepareQueued, PreparationCommandId);
        PreparationError = ClipPreparationError(error);
        PreparationStatus = ReleasePreparationStatus.Failed;
        PreparationRevision++;
        PreparationUpdatedAtUtc = DateTime.UtcNow;
        UpdatedAt = PreparationUpdatedAtUtc;
    }

    /// <summary>Queues VerifyLanding only on the workstation that created the prepared branch.</summary>
    public void BeginVerification(Guid deviceId, Guid commandId)
    {
        if (PreparationStatus is not (ReleasePreparationStatus.Prepared or ReleasePreparationStatus.VerifyFailed))
            throw new InvalidOperationException("Release must be Prepared or VerifyFailed before landing verification.");
        if (PreparationDeviceId is null || PreparationDeviceId != deviceId)
            throw new InvalidOperationException("VerifyLanding must run on the workstation pinned by Prepare.");
        if (commandId == Guid.Empty)
            throw new ArgumentException("Verification command id is required.", nameof(commandId));
        VerificationCommandId = commandId;
        PreparationError = null;
        PreparationStatus = ReleasePreparationStatus.VerifyQueued;
        PreparationRevision++;
        PreparationUpdatedAtUtc = DateTime.UtcNow;
        UpdatedAt = PreparationUpdatedAtUtc;
    }

    public void RecordVerificationFailure(Guid commandId, string error)
    {
        RequirePreparationCommand(commandId, ReleasePreparationStatus.VerifyQueued, VerificationCommandId);
        PreparationError = ClipPreparationError(error);
        PreparationStatus = ReleasePreparationStatus.VerifyFailed;
        PreparationRevision++;
        PreparationUpdatedAtUtc = DateTime.UtcNow;
        UpdatedAt = PreparationUpdatedAtUtc;
    }

    /// <summary>Associates immutable evidence only after the matching VerifyLanding command succeeds.</summary>
    public void MarkCandidate(ReleaseCandidateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Status != ReleaseStatus.InProgress
            || PreparationStatus != ReleasePreparationStatus.VerifyQueued)
            throw new InvalidOperationException("Only an InProgress release with a queued VerifyLanding command can become a Candidate.");
        if (snapshot.ProjectId != ProjectId || snapshot.ReleaseId != Id
            || snapshot.CandidateId != CandidateId
            || snapshot.PreparedByDeviceId != PreparationDeviceId
            || snapshot.ReleaseRevision != Revision
            || !string.Equals(TargetVersion?.Trim(), snapshot.Version, StringComparison.Ordinal)
            || !string.Equals(TargetBranch, snapshot.TargetBranch, StringComparison.Ordinal))
            throw new InvalidOperationException("Candidate snapshot must match this Release revision, target version/branch, Candidate id and pinned workstation.");
        if (CandidateSnapshot is not null)
            throw new InvalidOperationException("This Release already has an immutable Candidate snapshot.");
        CandidateSnapshot = snapshot;
        Status = ReleaseStatus.Candidate;
        PreparationStatus = ReleasePreparationStatus.Candidate;
        PreparationError = null;
        PreparationRevision++;
        PreparationUpdatedAtUtc = DateTime.UtcNow;
        Revision++;
        UpdatedAt = PreparationUpdatedAtUtc;
    }

    private void RequirePreparationCommand(
        Guid commandId, ReleasePreparationStatus expectedStatus, Guid? expectedCommandId)
    {
        if (commandId == Guid.Empty || PreparationStatus != expectedStatus || expectedCommandId != commandId)
            throw new InvalidOperationException("ReleasePrepare completion does not match the current command and lifecycle state.");
    }

    private static string ClipPreparationError(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return error.Length <= 4000 ? error : error[..4000];
    }

    /// <summary>Publication workflow transition after the immutable candidate and tag are verified.</summary>
    public void MarkPublished(DateTime publishedAtUtc)
    {
        if (Status != ReleaseStatus.Candidate)
            throw new InvalidOperationException("Only a Candidate release can be published.");
        if (publishedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Publication time must be UTC.", nameof(publishedAtUtc));

        Status = ReleaseStatus.Published;
        PublishedAt = publishedAtUtc;
        Revision++;
        UpdatedAt = publishedAtUtc;
    }

    public void MarkContentDeleted()
    {
        Revision++;
        UpdatedAt = DateTime.UtcNow;
    }
}
