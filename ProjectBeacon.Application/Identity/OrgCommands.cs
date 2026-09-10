namespace ProjectBeacon.Application.Identity;

using Application.Common;

public record CreateOrgRequest(string Name, string? Description, Guid? CreatedByUserId = null);

public record CreateOrgCommand(CreateOrgRequest Request) : ICommand<Result<OrgDto>>;

public record UpdateOrgRequest(Guid OrgId, string? Name, string? Description);

public record UpdateOrgCommand(UpdateOrgRequest Request) : ICommand<Result<OrgDto>>;

public record GetOrgRequest(Guid OrgId);

public record GetOrgCommand(GetOrgRequest Request) : ICommand<Result<OrgDto>>;

public record ListOrgsRequest;

public record ListOrgsCommand(ListOrgsRequest Request) : ICommand<Result<IList<OrgDto>>>;

public record OrgDto(
    Guid Id,
    string Name,
    string? Description,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
