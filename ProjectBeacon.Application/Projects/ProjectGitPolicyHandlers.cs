namespace ProjectBeacon.Application.Projects;

using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Audit;
using Application.Authorization;
using Application.Common;
using Application.Persistence;
using Domain.Entities.Projects;
using Microsoft.EntityFrameworkCore;

/// <summary>Project-owned landing automation policy. Unknown or invalid persisted values fail closed.</summary>
public sealed record ProjectGitPolicyDto(
    int AutonomyLevel,
    string DirtyCheckoutBehavior,
    IReadOnlyList<string> ProtectedPaths,
    int MaxChangedFiles,
    int MaxChangedLines,
    string AutoRevertMode,
    int Revision,
    bool IntegrationBranchAvailable,
    string? IntegrationBranchUnavailableReason,
    bool AutoSyncEnabled = false);

public sealed record SetProjectGitPolicyCommand(
    Guid ProjectId,
    int AutonomyLevel,
    string DirtyCheckoutBehavior,
    IReadOnlyList<string>? ProtectedPaths,
    int MaxChangedFiles,
    int MaxChangedLines,
    string AutoRevertMode,
    int ExpectedRevision,
    ActorContext Actor,
    bool? AutoSyncEnabled = null);

public static class ProjectGitPolicyCatalog
{
    public const string IntegrationBranchName = "beacon/integration";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex SafePattern = new(
        "^[A-Za-z0-9._/*?-]{1,200}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public const string DefaultJson =
        "{\"autonomyLevel\":0,\"dirtyCheckoutBehavior\":\"Escalate\",\"protectedPaths\":[],\"maxChangedFiles\":200,\"maxChangedLines\":20000,\"autoRevertMode\":\"Off\",\"autoSyncEnabled\":false}";

    public static ProjectGitPolicyDto Defaults(int revision = 1) =>
        new(0, "Escalate", [], 200, 20_000, "Off", revision, true, null, false);

    public static bool TryRead(string? json, int revision, out ProjectGitPolicyDto policy)
    {
        policy = Defaults(revision);
        if (string.IsNullOrWhiteSpace(json))
            return true;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var propertyNames = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (propertyNames.Distinct(StringComparer.Ordinal).Count() != propertyNames.Length)
                return false;
            var autoSyncEnabled = false;
            if (root.TryGetProperty("autoSyncEnabled", out var autoSyncElement))
            {
                if (autoSyncElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return false;
                autoSyncEnabled = autoSyncElement.GetBoolean();
            }

            if (!TryInt(root, "autonomyLevel", out var autonomy)
                || autonomy is < 0 or > 2
                || !TryString(root, "dirtyCheckoutBehavior", out var dirty)
                || dirty is not ("Escalate" or "LandViaIntegration")
                || !TryInt(root, "maxChangedFiles", out var files)
                || files is < 1 or > 200
                || !TryInt(root, "maxChangedLines", out var lines)
                || lines is < 1 or > 20_000
                || !TryString(root, "autoRevertMode", out var revert)
                || revert is not ("Off" or "OnPostCheckFail")
                || !root.TryGetProperty("protectedPaths", out var pathsElement)
                || pathsElement.ValueKind != JsonValueKind.Array
                || pathsElement.GetArrayLength() > 100)
                return false;

            var paths = new List<string>();
            foreach (var item in pathsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return false;
                var pattern = item.GetString();
                if (!IsSafePattern(pattern) || paths.Contains(pattern!, StringComparer.Ordinal))
                    return false;
                paths.Add(pattern!);
            }

            // A corrupt or unknown property is not silently ignored. This protects policy
            // decisions from a newer/older writer whose semantics are not understood here.
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "autonomyLevel", "dirtyCheckoutBehavior", "protectedPaths",
                "maxChangedFiles", "maxChangedLines", "autoRevertMode", "reviewBaseBranch", "autoSyncEnabled"
            };
            if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
                return false;
            if (root.TryGetProperty("reviewBaseBranch", out var reviewBase)
                && (reviewBase.ValueKind != JsonValueKind.String || !IsSafeBranchName(reviewBase.GetString())))
                return false;

            policy = new ProjectGitPolicyDto(
                autonomy, dirty, paths.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                files, lines, revert, Math.Max(1, revision), true, null, autoSyncEnabled);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsSafePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)
            || !SafePattern.IsMatch(pattern)
            || pattern.StartsWith("/", StringComparison.Ordinal)
            || pattern.Contains("..", StringComparison.Ordinal)
            || pattern.Contains('\\')
            || pattern.Contains(':')
            || pattern.Split('/').Any(segment => segment.Length == 0))
            return false;
        return true;
    }

    public static string Serialize(
        int autonomyLevel,
        IReadOnlyList<string> protectedPaths,
        int maxChangedFiles,
        int maxChangedLines,
        string autoRevertMode,
        string dirtyCheckoutBehavior = "Escalate",
        bool autoSyncEnabled = false) =>
        JsonSerializer.Serialize(new
        {
            autonomyLevel,
            dirtyCheckoutBehavior,
            protectedPaths = protectedPaths.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            maxChangedFiles,
            maxChangedLines,
            autoRevertMode,
            autoSyncEnabled
        }, JsonOptions);

    public static string SerializeAttemptSnapshot(ProjectGitPolicyDto policy, string reviewBaseBranch)
    {
        if (!IsSafeBranchName(reviewBaseBranch))
            throw new ArgumentException("Review base branch is invalid.", nameof(reviewBaseBranch));
        return JsonSerializer.Serialize(new
        {
            autonomyLevel = policy.AutonomyLevel,
            dirtyCheckoutBehavior = policy.DirtyCheckoutBehavior,
            protectedPaths = policy.ProtectedPaths.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            maxChangedFiles = policy.MaxChangedFiles,
            maxChangedLines = policy.MaxChangedLines,
            autoRevertMode = policy.AutoRevertMode,
            reviewBaseBranch
        }, JsonOptions);
    }

    public static bool TryReadReviewBaseBranch(string? json, string? fallbackBranch, out string reviewBaseBranch)
    {
        reviewBaseBranch = fallbackBranch ?? string.Empty;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (root.TryGetProperty("reviewBaseBranch", out var property))
            {
                if (property.ValueKind != JsonValueKind.String)
                    return false;
                reviewBaseBranch = property.GetString() ?? string.Empty;
            }
            return IsSafeBranchName(reviewBaseBranch);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSafeBranchName(string? value) =>
        value is { Length: > 0 and <= 200 }
        && !value.StartsWith("/", StringComparison.Ordinal)
        && !value.EndsWith("/", StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains("@{", StringComparison.Ordinal)
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-' or '/');

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = default;
        return root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }
}

/// <summary>Reads the project landing policy for any project member.</summary>
public sealed class GetProjectGitPolicyHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;

    public GetProjectGitPolicyHandler(IBeaconDbFactory dbFactory, IAuthorizationService authorization)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
    }

    public async Task<Result<ProjectGitPolicyDto>> HandleAsync(
        Guid projectId, ActorContext actor, CancellationToken ct = default)
    {
        if (!actor.IsHuman || actor.UserId is null)
            return Result.Forbidden<ProjectGitPolicyDto>();
        await using var db = _dbFactory.CreateDbContext();
        var member = await db.ProjectMembers.AnyAsync(
            item => item.ProjectId == projectId && item.UserId == actor.UserId, ct);
        if (!member)
        {
            var administrator = await _authorization.CanAsync(
                db, actor, ResourceType.Project, AuthAction.Administer,
                projectId: projectId, ct: ct);
            if (!administrator.Success)
                return Result.Forbidden<ProjectGitPolicyDto>();
        }
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == projectId, ct);
        if (project is null)
            return Result.Failure<ProjectGitPolicyDto>("Project not found.", ErrorKind.NotFound);
        if (!ProjectGitPolicyCatalog.TryRead(project.GitPolicyJson, project.GitPolicyRevision, out var policy))
            return Result.Failure<ProjectGitPolicyDto>(
                "Stored Git policy is invalid. Landing automation remains disabled until an Owner/Admin repairs it.",
                ErrorKind.Conflict);
        return Result.Ok(policy);
    }
}

/// <summary>Updates the project Git policy. Only project or organization Owner/Admin may change it.</summary>
public sealed class SetProjectGitPolicyHandler
{
    private readonly IBeaconDbFactory _dbFactory;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditWriter _auditWriter;

    public SetProjectGitPolicyHandler(
        IBeaconDbFactory dbFactory,
        IAuthorizationService authorization,
        IAuditWriter auditWriter)
    {
        _dbFactory = dbFactory;
        _authorization = authorization;
        _auditWriter = auditWriter;
    }

    public async Task<Result<ProjectGitPolicyDto>> HandleAsync(
        SetProjectGitPolicyCommand command, CancellationToken ct = default)
    {
        if (!command.Actor.IsHuman || command.Actor.UserId is null)
            return Result.Forbidden<ProjectGitPolicyDto>("Only a human Owner/Admin can change the project Git policy.");
        if (command.AutonomyLevel is not (0 or 1 or 2))
            return Result.Failure<ProjectGitPolicyDto>(
                "Autonomy level must be 0, 1, or 2.",
                ErrorKind.Validation);
        if (command.DirtyCheckoutBehavior is not ("Escalate" or "LandViaIntegration"))
            return Result.Failure<ProjectGitPolicyDto>(
                "Dirty checkout behavior must be Escalate or LandViaIntegration.",
                ErrorKind.Validation);
        if (command.AutoRevertMode is not ("Off" or "OnPostCheckFail"))
            return Result.Failure<ProjectGitPolicyDto>("Auto-revert must be Off or OnPostCheckFail.", ErrorKind.Validation);
        if (command.MaxChangedFiles is < 1 or > 200 || command.MaxChangedLines is < 1 or > 20_000)
            return Result.Failure<ProjectGitPolicyDto>(
                "Diff limits must be between 1 and 200 files and between 1 and 20000 changed lines.",
                ErrorKind.Validation);

        var paths = (command.ProtectedPaths ?? []).Select(value => value?.Trim() ?? string.Empty).ToArray();
        if (paths.Length > 100 || paths.Any(value => !ProjectGitPolicyCatalog.IsSafePattern(value))
            || paths.Distinct(StringComparer.Ordinal).Count() != paths.Length)
            return Result.Failure<ProjectGitPolicyDto>(
                "Protected path patterns must be unique, project-relative simple globs (max 100, 200 characters each).",
                ErrorKind.Validation);

        await using var db = _dbFactory.CreateDbContext();
        var permission = await _authorization.CanAsync(
            db, command.Actor, ResourceType.Project, AuthAction.Administer,
            projectId: command.ProjectId, ct: ct);
        if (!permission.Success)
            return Result.Forbidden<ProjectGitPolicyDto>(
                "Only a project or organization Owner/Admin can change the project Git policy.");

        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == command.ProjectId, ct);
        if (project is null)
            return Result.Failure<ProjectGitPolicyDto>("Project not found.", ErrorKind.NotFound);
        if (command.ExpectedRevision < 1 || command.ExpectedRevision != project.GitPolicyRevision)
            return Result.Failure<ProjectGitPolicyDto>(
                "Git policy changed since it was loaded. Refresh the settings and retry.", ErrorKind.Conflict);

        if (!ProjectGitPolicyCatalog.TryRead(project.GitPolicyJson, project.GitPolicyRevision, out var current))
            return Result.Failure<ProjectGitPolicyDto>(
                "Stored Git policy is invalid. It must be repaired before it can be changed.", ErrorKind.Conflict);

        var autoSyncEnabled = command.AutoSyncEnabled ?? current.AutoSyncEnabled;
        if (current.AutonomyLevel == command.AutonomyLevel
            && current.DirtyCheckoutBehavior == command.DirtyCheckoutBehavior
            && current.AutoRevertMode == command.AutoRevertMode
            && current.MaxChangedFiles == command.MaxChangedFiles
            && current.MaxChangedLines == command.MaxChangedLines
            && current.AutoSyncEnabled == autoSyncEnabled
            && current.ProtectedPaths.SequenceEqual(paths.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
            return Result.Ok(current);

        project.SetGitPolicyJson(ProjectGitPolicyCatalog.Serialize(
            command.AutonomyLevel, paths, command.MaxChangedFiles, command.MaxChangedLines,
            command.AutoRevertMode, command.DirtyCheckoutBehavior, autoSyncEnabled));
        _auditWriter.Stage(db, AuditEventFactory.ForActor(
            command.Actor,
            "project.git_policy.updated",
            "ProjectGitPolicy",
            project.Id.ToString("D"),
            project.OrgId,
            project.Id,
            detailsJson: JsonSerializer.Serialize(new
            {
                previousState = $"Level{current.AutonomyLevel}",
                newState = $"Level{command.AutonomyLevel}",
                policyCode = command.AutoRevertMode == "OnPostCheckFail" ? "AutoRevert.OnPostCheckFail" : "AutoRevert.Off",
                dirtyCheckoutBehavior = command.DirtyCheckoutBehavior,
                autoSyncEnabled
            })));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure<ProjectGitPolicyDto>(
                "Git policy changed concurrently. Refresh the settings and retry.", ErrorKind.Conflict);
        }

        return Result.Ok(new ProjectGitPolicyDto(
            command.AutonomyLevel, command.DirtyCheckoutBehavior,
            paths.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            command.MaxChangedFiles, command.MaxChangedLines, command.AutoRevertMode,
            project.GitPolicyRevision, true, null, autoSyncEnabled));
    }
}
