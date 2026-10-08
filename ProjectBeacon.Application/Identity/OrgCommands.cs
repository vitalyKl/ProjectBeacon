namespace ProjectBeacon.Application.Identity;

using Application.Authorization;
using Application.Common;
/// <summary>
/// Name and optional description for a new org. The caller becomes owner.
/// </summary>
public record CreateOrgRequest(string Name, string? Description);
/// <summary>
/// Command for create org.
/// </summary>
public record CreateOrgCommand(CreateOrgRequest Request, ActorContext Actor) : ICommand<Result<OrgDto>>;
/// <summary>
/// Fields for update org.
/// </summary>
public record UpdateOrgRequest(Guid OrgId, string? Name, string? Description);
/// <summary>
/// Command for update org.
/// </summary>
public record UpdateOrgCommand(UpdateOrgRequest Request) : ICommand<Result<OrgDto>>;
/// <summary>
/// Fields for get org.
/// </summary>
public record GetOrgRequest(Guid OrgId);
/// <summary>
/// Command for get org.
/// </summary>
public record GetOrgCommand(GetOrgRequest Request) : ICommand<Result<OrgDto>>;
/// <summary>
/// Fields for list orgs.
/// </summary>
public record ListOrgsRequest;
/// <summary>
/// Command for list orgs.
/// </summary>
public record ListOrgsCommand(ListOrgsRequest Request) : ICommand<Result<IList<OrgDto>>>;
/// <summary>
/// An org's id, name, description, and timestamps.
/// </summary>
public record OrgDto(
    Guid Id,
    string Name,
    string? Description,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
