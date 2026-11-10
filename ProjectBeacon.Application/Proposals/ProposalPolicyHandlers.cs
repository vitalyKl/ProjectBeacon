namespace ProjectBeacon.Application.Proposals;

using System.Text.Json;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Projects;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>One target/operation route in the project's proposal decision matrix.</summary>
public sealed record ProposalPolicyDto(
    ContentTargetKind TargetKind,
    ProposalOperation Operation,
    ProposalDecisionMode Mode,
    bool Supported,
    bool AutoAllowed,
    string? RestrictionReason,
    int PolicyRevision);

/// <summary>Changes the project's decision mode for one content target and operation.</summary>
public sealed record SetProposalPolicyCommand(
    Guid ProjectId,
    ContentTargetKind TargetKind,
    ProposalOperation Operation,
    ProposalDecisionMode Mode,
    int ExpectedPolicyRevision,
    ActorContext Actor);

/// <summary>Defines the supported target/operation matrix and fails closed when its JSON is malformed.</summary>
public static class ProposalPolicyCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsSupported(ContentTargetKind targetKind, ProposalOperation operation) =>
        Enum.IsDefined(targetKind)
        && Enum.IsDefined(operation)
        && operation == ProposalOperation.Update;

    /// <summary>
    /// Automatic proposal application is restricted to documentation RepoPatch updates.
    /// Accepted content and governance updates always require human review.
    /// </summary>
    public static bool CanAutoApply(ContentTargetKind targetKind, ProposalOperation operation) =>
        targetKind == ContentTargetKind.RepoPatch && operation == ProposalOperation.Update;

    public static string RestrictionReason(ContentTargetKind targetKind, ProposalOperation operation)
    {
        if (operation != ProposalOperation.Update)
            return "Create and Delete proposals are not implemented in P2.";
        if (targetKind is ContentTargetKind.Decision or ContentTargetKind.Constraint)
            return "Decision and Constraint proposals always require an Owner/Admin decision.";
        if (targetKind == ContentTargetKind.RepoPatch)
            return "Repository patches require explicit documentation profile and path grants before Auto can be used.";
        if (operation == ProposalOperation.Update && IsSupported(targetKind, operation))
            return "Updates to existing accepted content always require manual human review.";
        return "This target/operation pair is not currently supported.";
    }

    public static string Key(ContentTargetKind targetKind, ProposalOperation operation) =>
        $"{targetKind}:{operation}";

    public static ProposalDecisionMode GetMode(
        string policiesJson,
        ContentTargetKind targetKind,
        ProposalOperation operation)
    {
        if (!TryRead(policiesJson, out var policies))
            return ProposalDecisionMode.Manual;
        return policies.TryGetValue(Key(targetKind, operation), out var configured)
            && Enum.TryParse<ProposalDecisionMode>(configured, ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
                ? mode
                : ProposalDecisionMode.Manual;
    }

    public static bool TryRead(string? policiesJson, out Dictionary<string, string> policies)
    {
        policies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(policiesJson))
            return true;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(policiesJson, JsonOptions);
            if (parsed is null)
                return false;

            foreach (var pair in parsed)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)
                    || !Enum.TryParse<ProposalDecisionMode>(pair.Value, true, out var mode)
                    || !Enum.IsDefined(mode))
                    return false;
                policies[pair.Key] = mode.ToString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Serialize(IReadOnlyDictionary<string, string> policies) =>
        JsonSerializer.Serialize(
            policies.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            JsonOptions);

    public static IReadOnlyList<ProposalPolicyDto> BuildMatrix(Project project)
    {
        var valid = TryRead(project.ProposalPoliciesJson, out var policies);
        return Enum.GetValues<ContentTargetKind>()
            .SelectMany(target => Enum.GetValues<ProposalOperation>().Select(operation =>
            {
                var supported = IsSupported(target, operation);
                var autoAllowed = CanAutoApply(target, operation);
                var mode = valid
                    ? GetMode(project.ProposalPoliciesJson, target, operation)
                    : ProposalDecisionMode.Manual;
                if (!autoAllowed)
                    mode = ProposalDecisionMode.Manual;

                return new ProposalPolicyDto(
                    target,
                    operation,
                    mode,
                    supported,
                    autoAllowed,
                    autoAllowed ? null : RestrictionReason(target, operation),
                    project.ProposalPolicyRevision);
            }))
            .ToArray();
    }
}

/// <summary>Reads the complete matrix for a project member.</summary>
public sealed class ListProposalPoliciesHandler
{
    private readonly IBeaconDbFactory _dbFactory;

    public ListProposalPoliciesHandler(IBeaconDbFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<Result<IReadOnlyList<ProposalPolicyDto>>> HandleAsync(
        Guid projectId,
        ActorContext actor,
        CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<IReadOnlyList<ProposalPolicyDto>>();

        await using var db = _dbFactory.CreateDbContext();
        if (!await ProposalProjectAuthorization.CanWriteAsync(db, actor, projectId, ct))
            return Result.Forbidden<IReadOnlyList<ProposalPolicyDto>>();

        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, ct);
        if (project is null)
            return Result.Failure<IReadOnlyList<ProposalPolicyDto>>("Project not found.");

        return Result.Ok(ProposalPolicyCatalog.BuildMatrix(project));
    }
}

/// <summary>Allows only a project/org Owner/Admin to change a supported decision policy.</summary>
public sealed class SetProposalPolicyHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;

    public SetProposalPolicyHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
    }

    public async Task<Result<ProposalPolicyDto>> HandleAsync(
        SetProposalPolicyCommand command,
        CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<ProposalPolicyDto>("Only a human Owner/Admin can change proposal policies.");
        if (!Enum.IsDefined(command.TargetKind)
            || !Enum.IsDefined(command.Operation)
            || !Enum.IsDefined(command.Mode))
            return Result.Failure<ProposalPolicyDto>("Unknown proposal policy value.");
        if (!ProposalPolicyCatalog.IsSupported(command.TargetKind, command.Operation))
            return Result.Failure<ProposalPolicyDto>(
                ProposalPolicyCatalog.RestrictionReason(command.TargetKind, command.Operation));
        if (command.Mode == ProposalDecisionMode.Auto
            && !ProposalPolicyCatalog.CanAutoApply(command.TargetKind, command.Operation))
            return Result.Failure<ProposalPolicyDto>(
                ProposalPolicyCatalog.RestrictionReason(command.TargetKind, command.Operation));

        await using var db = _dbFactory.CreateDbContext();
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == command.ProjectId, ct);
        if (project is null)
            return Result.Failure<ProposalPolicyDto>("Project not found.");

        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Project, AuthAction.Administer,
            projectId: command.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Forbidden<ProposalPolicyDto>(
                "Only a project or organization Owner/Admin can change proposal policies.");

        if (command.ExpectedPolicyRevision < 1
            || command.ExpectedPolicyRevision != project.ProposalPolicyRevision)
            return Result.Failure<ProposalPolicyDto>(
                "Proposal policy changed since it was loaded. Refresh the matrix and retry.",
                ErrorKind.Conflict);

        if (!ProposalPolicyCatalog.TryRead(project.ProposalPoliciesJson, out var policies))
            return Result.Failure<ProposalPolicyDto>(
                "Stored proposal policy settings are invalid. Manual review remains in effect.",
                ErrorKind.Conflict);

        var key = ProposalPolicyCatalog.Key(command.TargetKind, command.Operation);
        var previous = policies.TryGetValue(key, out var configured)
            && Enum.TryParse<ProposalDecisionMode>(configured, true, out var parsed)
                ? parsed
                : ProposalDecisionMode.Manual;
        if (previous == command.Mode)
            return Result.Ok(ProposalPolicyCatalog.BuildMatrix(project)
                .Single(item => item.TargetKind == command.TargetKind && item.Operation == command.Operation));

        policies[key] = command.Mode.ToString();
        project.SetProposalPoliciesJson(ProposalPolicyCatalog.Serialize(policies));

        var orgId = project.OrgId;
        _auditWriter.Stage(db, AuditEventFactory.ForActor(
            command.Actor,
            "proposal.policy.updated",
            "ProposalPolicy",
            key,
            orgId,
            command.ProjectId,
            detailsJson: JsonSerializer.Serialize(new
            {
                previousState = previous.ToString(),
                newState = command.Mode.ToString(),
                policyCode = key.Replace(':', '.')
            })));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ProposalPolicyDto>(
                "Proposal policy changed while it was being saved. Refresh the matrix and retry.",
                ErrorKind.Conflict);
        }

        return Result.Ok(ProposalPolicyCatalog.BuildMatrix(project)
            .Single(item => item.TargetKind == command.TargetKind && item.Operation == command.Operation));
    }
}
