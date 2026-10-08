namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
/// <summary>
/// One context section, including scope, source, and body.
/// </summary>
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
/// <summary>
/// Fields for creating or replacing a context section. Source defaults to Native in the handler when omitted on the wire.
/// </summary>
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
/// <summary>
/// Command for upsert context node.
/// </summary>
public record UpsertContextNodeCommand(UpsertContextNodeRequest Request) : ICommand<Result<ContextSectionDto>>;
/// <summary>
/// Fields for list context nodes.
/// </summary>
public record ListContextNodesRequest(Guid ProjectId);
/// <summary>
/// Command for list context nodes.
/// </summary>
public record ListContextNodesCommand(ListContextNodesRequest Request) : IQuery<IList<ContextSectionDto>>;
/// <summary>
/// Fields for get context node.
/// </summary>
public record GetContextNodeRequest(Guid ProjectId, Guid NodeId);
/// <summary>
/// Command for get context node.
/// </summary>
public record GetContextNodeCommand(GetContextNodeRequest Request) : IQuery<Result<ContextSectionDto>>;
/// <summary>
/// Fields for delete context node.
/// </summary>
public record DeleteContextNodeRequest(Guid ProjectId, Guid NodeId);
/// <summary>
/// Command for delete context node.
/// </summary>
public record DeleteContextNodeCommand(DeleteContextNodeRequest Request) : ICommand<Result>;
