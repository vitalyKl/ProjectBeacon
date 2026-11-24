namespace ProjectBeacon.Application.Releases;

using Application.Authorization;
using Domain.Enums;

public sealed record ReleaseDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    string? TargetVersion,
    string TargetBranch,
    ReleaseStatus Status,
    int Order,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? PublishedAt,
    int Revision,
    int MilestoneCount,
    ReleasePreparationStatus PreparationStatus = ReleasePreparationStatus.NotStarted,
    int PreparationRevision = 1,
    Guid? PreparationDeviceId = null,
    Guid? CandidateId = null,
    Guid? PreparationCommandId = null,
    Guid? VerificationCommandId = null,
    string? PreparationResultJson = null,
    string? PreparationError = null,
    ReleasePublicationStatus PublicationStatus = ReleasePublicationStatus.NotStarted,
    int PublicationRevision = 1,
    bool PublicationTagPushed = false,
    string? PublicationError = null,
    string? PublishedTagName = null,
    long? DeploymentWorkflowRunId = null,
    string? DeploymentWorkflowRunUrl = null,
    string? DeployedVersion = null,
    string? DeployedCommitSha = null,
    DateTime? DeployedAtUtc = null);

public sealed record CreateReleaseRequest(
    string Name,
    string? Description,
    string? TargetVersion,
    string TargetBranch,
    int Order);

public sealed record CreateReleaseCommand(Guid ProjectId, CreateReleaseRequest Request, ActorContext Actor);
public sealed record UpdateReleaseRequest(
    Guid ReleaseId, Guid ProjectId, string? Name, string? Description, string? TargetVersion,
    string? TargetBranch, int? Order, int ExpectedRevision);
public sealed record UpdateReleaseCommand(UpdateReleaseRequest Request, ActorContext Actor);
public sealed record SetReleaseStatusRequest(Guid ReleaseId, Guid ProjectId, ReleaseStatus Status, int ExpectedRevision);
public sealed record SetReleaseStatusCommand(SetReleaseStatusRequest Request, ActorContext Actor);
public sealed record DeleteReleaseRequest(Guid ReleaseId, Guid ProjectId);
public sealed record DeleteReleaseCommand(DeleteReleaseRequest Request, ActorContext Actor);
public sealed record GetReleaseRequest(Guid ReleaseId, Guid ProjectId);
public sealed record GetReleaseCommand(GetReleaseRequest Request, ActorContext Actor);
public sealed record ListProjectReleasesRequest(Guid ProjectId);
public sealed record ListProjectReleasesCommand(ListProjectReleasesRequest Request, ActorContext Actor);

public sealed record SetMilestoneReleaseRequest(
    Guid MilestoneId, Guid ProjectId, Guid? ReleaseId, int ExpectedRevision);
public sealed record SetMilestoneReleaseCommand(SetMilestoneReleaseRequest Request, ActorContext Actor);
