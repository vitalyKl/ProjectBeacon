namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

public record ImportFileRequest(
    string FileName,
    string Content,
    string SourcePath);

public record ImportFilesCommand(Guid ProjectId, IList<ImportFileRequest> Files) : ICommand<Result<int>>;

public record ExportAgentsMdRequest(Guid ProjectId, Guid? RepoId, string? Path);

public record ExportAgentsMdCommand(ExportAgentsMdRequest Request) : ICommand<Result<string>>;
