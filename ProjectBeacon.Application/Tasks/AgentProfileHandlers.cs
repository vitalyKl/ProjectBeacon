namespace ProjectBeacon.Application.Tasks;

using Application.Common;
using Application.Persistence;
using Domain.Entities.Agents;
using Microsoft.EntityFrameworkCore;

/// <summary>Lists the current user's saved overrides without changing the code-owned catalogue.</summary>
public sealed class GetAgentProfileOverridesHandler
    : ICommandHandler<GetAgentProfileOverridesCommand, Result<IReadOnlyList<AgentProfileOverrideDto>>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public GetAgentProfileOverridesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IReadOnlyList<AgentProfileOverrideDto>>> HandleAsync(
        GetAgentProfileOverridesCommand command, CancellationToken ct = default)
    {
        if (command.UserId == Guid.Empty)
            return Result.Failure<IReadOnlyList<AgentProfileOverrideDto>>("Account is not resolved.");

        await using var db = _dbFactory.CreateDbContext();
        var overrides = await db.AgentProfiles
            .Where(profile => profile.UserId == command.UserId)
            .OrderBy(profile => profile.Key)
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<AgentProfileOverrideDto>>(overrides.Select(ToDto).ToList());
    }

    internal static AgentProfileOverrideDto ToDto(AgentProfile profile) =>
        new(profile.UserId, profile.Key, profile.Name, profile.Description, profile.Instruction,
            profile.Mode, profile.Tools, profile.PathPolicy, profile.DefaultPaths,
            profile.CommandsEnabled, profile.ReportsDiff, profile.SizeHint, profile.Effort);
}

/// <summary>
/// Saves a user's preferences for a built-in profile. The code-owned profile remains the authority
/// for required tools, command capability, execution mode, and path grants.
/// </summary>
public sealed class SaveAgentProfileOverrideHandler
    : ICommandHandler<SaveAgentProfileOverrideCommand, Result<AgentProfileOverrideDto>>
{
    private readonly IBeaconDbFactory _dbFactory;

    public SaveAgentProfileOverrideHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<AgentProfileOverrideDto>> HandleAsync(
        SaveAgentProfileOverrideCommand command, CancellationToken ct = default)
    {
        var request = command.Request;
        if (request.UserId == Guid.Empty)
            return Result.Failure<AgentProfileOverrideDto>("Account is not resolved.");

        var basis = BuiltInAgentProfiles.Get(request.Key);
        if (basis is null)
            return Result.Failure<AgentProfileOverrideDto>("Built-in execution profile not found.");

        var name = request.Name?.Trim() ?? string.Empty;
        var description = request.Description?.Trim() ?? string.Empty;
        var instruction = request.Instruction?.Trim() ?? string.Empty;
        var sizeHint = request.SizeHint?.Trim() ?? string.Empty;
        var effort = request.Effort?.Trim() ?? string.Empty;

        if (name.Length is 0 or > 120)
            return Result.Failure<AgentProfileOverrideDto>("Profile name is required (max 120 characters).");
        if (description.Length > 300)
            return Result.Failure<AgentProfileOverrideDto>("Profile description cannot exceed 300 characters.");
        if (instruction.Length is 0 or > 8000)
            return Result.Failure<AgentProfileOverrideDto>("Profile instructions are required (max 8000 characters).");
        if (sizeHint.Length is 0 or > 200)
            return Result.Failure<AgentProfileOverrideDto>("Profile size hint is required (max 200 characters).");
        if (effort.Length is 0 or > 24)
            return Result.Failure<AgentProfileOverrideDto>("Profile effort is required (max 24 characters).");
        if (request.CommandsEnabled && !basis.CommandsEnabled)
            return Result.Failure<AgentProfileOverrideDto>("This built-in profile does not permit command execution.");

        var selectedTools = (request.Tools ?? [])
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .Select(tool => tool.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unsupportedTool = selectedTools.FirstOrDefault(
            tool => !basis.Tools.Contains(tool, StringComparer.Ordinal));
        if (unsupportedTool is not null)
            return Result.Failure<AgentProfileOverrideDto>(
                $"Tool '{unsupportedTool}' is outside the built-in profile's tool grants.");

        // Preserve catalogue order and let ApplyOverride re-add mandatory lifecycle tools.
        var narrowedTools = basis.Tools.Where(tool => selectedTools.Contains(tool, StringComparer.Ordinal)).ToArray();

        await using var db = _dbFactory.CreateDbContext();
        var existing = await db.AgentProfiles.FirstOrDefaultAsync(
            profile => profile.UserId == request.UserId && profile.Key == basis.Key, ct);
        if (existing is null)
        {
            existing = AgentProfile.Create(
                request.UserId, basis.Key, name, description, instruction, basis.Mode.ToString(),
                narrowedTools, basis.PathPolicy.ToString(), basis.DefaultPaths,
                request.CommandsEnabled, request.ReportsDiff, sizeHint, effort);
            db.AgentProfiles.Add(existing);
        }
        else
        {
            existing.Update(
                name, description, instruction, basis.Mode.ToString(), narrowedTools,
                basis.PathPolicy.ToString(), basis.DefaultPaths,
                request.CommandsEnabled, request.ReportsDiff, sizeHint, effort);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(GetAgentProfileOverridesHandler.ToDto(existing));
    }
}

/// <summary>Removes one user's override so future subtasks use the immutable built-in definition.</summary>
public sealed class ResetAgentProfileOverrideHandler
    : ICommandHandler<ResetAgentProfileOverrideCommand, Result>
{
    private readonly IBeaconDbFactory _dbFactory;

    public ResetAgentProfileOverrideHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result> HandleAsync(ResetAgentProfileOverrideCommand command, CancellationToken ct = default)
    {
        if (command.Request.UserId == Guid.Empty)
            return Result.Failure("Account is not resolved.");

        if (BuiltInAgentProfiles.Get(command.Request.Key) is null)
            return Result.Failure("Built-in execution profile not found.");

        await using var db = _dbFactory.CreateDbContext();
        var existing = await db.AgentProfiles.FirstOrDefaultAsync(
            profile => profile.UserId == command.Request.UserId && profile.Key == command.Request.Key, ct);
        if (existing is null)
            return Result.Ok();

        db.AgentProfiles.Remove(existing);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
