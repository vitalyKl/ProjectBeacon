namespace ProjectBeacon.Application.Context;

using Application.Common;
using Domain.Entities.Projects;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// One markdown file to import: file name, content, and the path it came from.
/// </summary>
public record ImportFileRequest(
    string FileName,
    string Content,
    string SourcePath);
/// <summary>
/// Imports markdown files into a project as context sections. The result is how many were created.
/// </summary>
public record ImportFilesCommand(Guid ProjectId, IList<ImportFileRequest> Files) : ICommand<Result<int>>;
/// <summary>
/// Project, optional repo, and optional path for an AGENTS.md export.
/// </summary>
public record ExportAgentsMdRequest(Guid ProjectId, Guid? RepoId, string? Path);
/// <summary>
/// Command for export agents md.
/// </summary>
public record ExportAgentsMdCommand(ExportAgentsMdRequest Request) : ICommand<Result<string>>;
