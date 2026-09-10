namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;

public record ContextSectionDto(
    Guid Id,
    string SectionId,
    string Key,
    string Title,
    string BodyMarkdown,
    int Ordinal,
    Guid ProjectId,
    Guid? RepoId,
    Guid? TaskId,
    ContextScopeType ScopeType,
    string Path,
    string SectionsText,
    ContextSource Source,
    string? SourcePath,
    ContextReviewState ReviewState,
    string? UpdatedByType,
    string? UpdatedById,
    DateTime UpdatedAt);

public record UpsertContextNodeRequest(
    Guid ProjectId,
    string Title,
    string BodyMarkdown,
    string? SectionId,
    ContextScopeType ScopeType,
    string? Key,
    Guid? RepoId,
    Guid? TaskId,
    string? Path,
    ContextSource Source,
    string? SourcePath);

public record UpsertContextNodeCommand(UpsertContextNodeRequest Request) : ICommand<Result<ContextSectionDto>>;

public record ListContextNodesRequest(Guid ProjectId);

public record ListContextNodesCommand(ListContextNodesRequest Request) : IQuery<IList<ContextSectionDto>>;

public record GetContextNodeRequest(Guid ProjectId, Guid NodeId);

public record GetContextNodeCommand(GetContextNodeRequest Request) : IQuery<Result<ContextSectionDto>>;

public record DeleteContextNodeRequest(Guid ProjectId, Guid NodeId);

public record DeleteContextNodeCommand(DeleteContextNodeRequest Request) : ICommand<Result<bool>>;
