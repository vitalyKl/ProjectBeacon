namespace ProjectBeacon.API.Auth;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Authorization;
using Application.Devices;
using Domain.Entities.Evals;
using Domain.Entities.Identity;
using Domain.Entities.Tasks;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Authenticates <c>Bearer bcn_</c> API tokens and <c>Bearer bcd_</c> device tokens by replacing
/// <c>HttpContext.User</c> before later middleware. Harness credentials are additionally bound to
/// the live workstation command and, for pipeline/eval commands, to their persisted execution scope.
/// </summary>
public sealed class ApiTokenAuthMiddleware
{
    private const ApiTokenCapability ChatAndPipelineCapabilities =
        ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite;

    private const ApiTokenCapability EvalCapabilities =
        ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead;

    private readonly RequestDelegate _next;

    public ApiTokenAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, BeaconDbContext db)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer bcn_", StringComparison.OrdinalIgnoreCase))
        {
            var raw = header["Bearer ".Length..].Trim();
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            var token = await db.ApiTokens.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.TokenHash == hash, ctx.RequestAborted);

            var now = DateTime.UtcNow;
            var valid = token is not null && !token.IsExpired && !token.IsRevoked;
            if (valid && token!.ClientKind == ApiTokenClientKind.Harness)
                valid = await IsHarnessTokenActiveAsync(db, token, ctx, now, ctx.RequestAborted);

            if (valid && token is not null)
            {
                // LastUsedAt is approximate: avoid a database write for every agent API call.
                if (token.LastUsedAt is null || token.LastUsedAt < now.AddMinutes(-5))
                {
                    token.RecordUsage();
                    await db.SaveChangesAsync(ctx.RequestAborted);
                }

                var identity = new ClaimsIdentity(ActorContextFactory.ApiTokenAuthType);
                identity.AddClaim(new Claim("token_id", token.Id.ToString()));
                identity.AddClaim(new Claim("project_id", token.ProjectId.ToString()));
                identity.AddClaim(new Claim("capabilities", ((long)token.Capabilities).ToString()));
                if (token.CreatedByUserId.HasValue)
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, token.CreatedByUserId.Value.ToString()));
                ctx.User = new ClaimsPrincipal(identity);
            }
        }
        else if (header.StartsWith("Bearer bcd_", StringComparison.OrdinalIgnoreCase))
        {
            var raw = header["Bearer ".Length..].Trim();
            var hash = DeviceToken.Hash(raw);
            var device = await db.DaemonDevices.FirstOrDefaultAsync(
                d => d.TokenHash == hash && d.RevokedAt == null, ctx.RequestAborted);
            if (device is not null)
            {
                var identity = new ClaimsIdentity(
                    ActorContextFactory.DeviceTokenAuthType, ClaimTypes.Name, ClaimTypes.Role);
                identity.AddClaim(new Claim("device_id", device.Id.ToString()));
                identity.AddClaim(new Claim("device_owner_id", device.UserId.ToString()));
                ctx.User = new ClaimsPrincipal(identity);
            }
        }

        await _next(ctx);
    }

    private static async Task<bool> IsHarnessTokenActiveAsync(
        BeaconDbContext db,
        ApiToken token,
        HttpContext context,
        DateTime utcNow,
        CancellationToken ct)
    {
        if (token.DeviceId is not { } deviceId
            || token.CommandId is not { } commandId
            || token.ProjectId == Guid.Empty)
            return false;

        var work = await db.WorkstationCommands.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.Id == commandId && c.DeviceId == deviceId)
            .Select(c => new
            {
                c.ProjectId,
                c.Kind,
                c.PayloadJson,
                c.Status,
                c.StartedAt,
                c.LeaseExpiresAt,
                c.ClaimInstanceId
            })
            .FirstOrDefaultAsync(ct);

        if (work is null
            || work.ProjectId != token.ProjectId
            || work.Kind is not (WorkstationCommandKind.ChatPrompt
                or WorkstationCommandKind.RunPipelineSession
                or WorkstationCommandKind.RunEvalTurn)
            || work.Status != WorkstationCommandStatus.Running
            || work.StartedAt is not { } startedAt
            || token.CreatedAt < startedAt
            || work.LeaseExpiresAt is not { } leaseExpiresAt
            || leaseExpiresAt <= utcNow)
            return false;

        var expectedCapabilities = work.Kind == WorkstationCommandKind.RunEvalTurn
            ? EvalCapabilities
            : ChatAndPipelineCapabilities;
        if (token.Capabilities != expectedCapabilities)
            return false;

        var device = await db.DaemonDevices.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null
            || device.IsRevoked
            || !device.IsOnline(utcNow)
            || !string.Equals(work.ClaimInstanceId, device.ActiveInstanceId, StringComparison.Ordinal))
            return false;

        var scope = await ResolveCommandScopeAsync(db, work.Kind, work.PayloadJson, token.ProjectId, deviceId, ct);
        if (scope is null)
            return false;

        Guid? launchSessionTaskId = null;
        PipelineRole? launchSessionRole = null;
        SessionStatus? launchSessionStatus = null;
        if (work.Kind == WorkstationCommandKind.RunPipelineSession
            && IsLaunchSessionRequest(context.Request.Method, context.Request.Path.Value))
        {
            if (!TryGetGuidSegment(context.Request.Path.Value, 2, out var launchedSessionId))
                return false;

            var launched = await db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Id == launchedSessionId && s.ProjectId == token.ProjectId)
                .Select(s => new { s.TaskId, s.Role, s.Status })
                .FirstOrDefaultAsync(ct);
            if (launched is null)
                return false;

            launchSessionTaskId = launched.TaskId;
            launchSessionRole = launched.Role;
            launchSessionStatus = launched.Status;
        }

        var allowed = IsHarnessRequestAllowed(
            work.Kind,
            scope.Role,
            token.ProjectId,
            scope.TaskId,
            scope.SubtaskId,
            context.Request.Method,
            context.Request.Path.Value,
            launchSessionTaskId,
            launchSessionRole,
            launchSessionStatus);
        if (!allowed)
            return false;

        // context_compile is a POST for transport reasons, but it is still a read operation.
        // Its taskId is part of the body, so pin it explicitly to the command's task rather
        // than relying on the client-provided tool arguments.
        if (work.Kind == WorkstationCommandKind.RunPipelineSession
            && scope.Role == PipelineRole.Planner
            && IsContextCompileRequest(context.Request.Method, context.Request.Path.Value))
            return await IsContextCompileBodyScopedAsync(context.Request, scope.TaskId, ct);

        return true;
    }

    private static async Task<HarnessCommandScope?> ResolveCommandScopeAsync(
        BeaconDbContext db,
        WorkstationCommandKind kind,
        string payloadJson,
        Guid projectId,
        Guid deviceId,
        CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (kind == WorkstationCommandKind.ChatPrompt)
            {
                if (!TryReadGuid(root, "chatSessionId", out var chatSessionId)
                    || !root.TryGetProperty("externalSessionId", out var external)
                    || external.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(external.GetString()))
                    return null;

                var chat = await db.ChatSessions.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == chatSessionId
                        && s.ProjectId == projectId
                        && s.DeviceId == deviceId
                        && s.ArchivedAt == null
                        && s.Status == ChatSessionStatus.Streaming
                        && s.ExternalSessionId == external.GetString(), ct);
                return chat is null
                    ? null
                    : new HarnessCommandScope(kind, projectId, null, null, null, null);
            }

            if (kind == WorkstationCommandKind.RunPipelineSession)
            {
                if (!TryReadGuid(root, "projectId", out var payloadProjectId)
                    || payloadProjectId != projectId
                    || !TryReadGuid(root, "taskId", out var taskId)
                    || !TryReadGuid(root, "sessionId", out var sessionId)
                    || !root.TryGetProperty("role", out var roleValue)
                    || roleValue.ValueKind != JsonValueKind.String
                    || !Enum.TryParse(roleValue.GetString(), true, out PipelineRole role)
                    || !Enum.IsDefined(typeof(PipelineRole), role)
                    || !TryReadNullableGuid(root, "subtaskId", out var subtaskId))
                    return null;

                var session = await db.PipelineSessions.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == sessionId && s.ProjectId == projectId, ct);
                var taskExists = await db.Tasks.IgnoreQueryFilters()
                    .AnyAsync(t => t.Id == taskId && t.ProjectId == projectId, ct);
                if (session is null
                    || !taskExists
                    || session.Status != SessionStatus.Active
                    || session.TaskId != taskId
                    || session.Role != role
                    || session.SubtaskId != subtaskId)
                    return null;

                if (role == PipelineRole.Actor)
                {
                    if (subtaskId is not { } actorSubtaskId)
                        return null;
                    var subtaskExists = await db.Subtasks.IgnoreQueryFilters()
                        .AnyAsync(s => s.Id == actorSubtaskId
                            && s.TaskId == taskId
                            && s.ProjectId == projectId
                            && s.Status == SubtaskStatus.InProgress, ct);
                    if (!subtaskExists)
                        return null;
                }
                else if (subtaskId is not null)
                {
                    return null;
                }

                return new HarnessCommandScope(kind, projectId, taskId, sessionId, role, subtaskId);
            }

            if (kind == WorkstationCommandKind.RunEvalTurn)
            {
                if (!TryReadGuid(root, "evalRunId", out var evalRunId)
                    || !TryReadGuid(root, "taskId", out var taskId)
                    || !root.TryGetProperty("runtime", out var runtimeValue)
                    || runtimeValue.ValueKind != JsonValueKind.String)
                    return null;

                var runtime = runtimeValue.GetString()?.Trim().ToLowerInvariant();
                if (runtime is not ("native" or "opencode"))
                    return null;

                var evalRun = await db.EvalRuns.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(run => run.Id == evalRunId
                        && run.ProjectId == projectId
                        && run.TaskId == taskId
                        && run.Runtime == runtime
                        && run.CompletedAt == null, ct);
                return evalRun is null
                    ? null
                    : new HarnessCommandScope(kind, projectId, taskId, null, null, null);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the server-side role boundary for a harness API token. MCP-side tool filtering is
    /// useful UX, but it is not an authorization boundary: callers can bypass MCP and invoke /v1
    /// directly. This method therefore maps each role onto the API routes it is allowed to call.
    /// </summary>
    internal static bool IsHarnessRequestAllowed(
        WorkstationCommandKind commandKind,
        PipelineRole? role,
        Guid projectId,
        Guid? taskId,
        Guid? subtaskId,
        string method,
        string? path,
        Guid? launchSessionTaskId = null,
        PipelineRole? launchSessionRole = null,
        SessionStatus? launchSessionStatus = null)
    {
        if (projectId == Guid.Empty || string.IsNullOrWhiteSpace(path))
            return false;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[0] != "v1")
            return false;

        if (commandKind == WorkstationCommandKind.ChatPrompt)
            return true;

        if (commandKind == WorkstationCommandKind.RunEvalTurn)
            return IsReadOnlyProjectRequest(segments, projectId, taskId, method, includePipelineStatus: false);

        if (commandKind != WorkstationCommandKind.RunPipelineSession || taskId is not { } scopedTaskId || role is null)
            return false;

        if (role == PipelineRole.Actor)
        {
            return method == HttpMethods.Post
                && segments.Length == 6
                && segments[1] == "tasks"
                && GuidSegmentEquals(segments, 2, scopedTaskId)
                && segments[3] == "subtasks"
                && subtaskId is { } actorSubtaskId
                && GuidSegmentEquals(segments, 4, actorSubtaskId)
                && segments[5] is "result" or "fail";
        }

        if (role == PipelineRole.Planner)
        {
            if (IsReadOnlyProjectRequest(segments, projectId, taskId, method, includePipelineStatus: true))
                return true;

            if (method == HttpMethods.Post
                && segments.Length == 5
                && segments[1] == "projects"
                && GuidSegmentEquals(segments, 2, projectId)
                && segments[3] == "context"
                && segments[4] == "compile")
                return true;

            if (method == HttpMethods.Post
                && segments.Length == 4
                && segments[1] == "tasks"
                && GuidSegmentEquals(segments, 2, scopedTaskId)
                && segments[3] == "subtasks")
                return true;

            if (method == HttpMethods.Post
                && segments.Length == 6
                && segments[1] == "tasks"
                && GuidSegmentEquals(segments, 2, scopedTaskId)
                && segments[3] == "subtasks"
                && segments[5] == "session")
                return true;

            if (method == HttpMethods.Post
                && segments.Length == 4
                && segments[1] == "sessions"
                && segments[3] == "launch"
                && Guid.TryParse(segments[2], out _)
                && launchSessionTaskId == scopedTaskId
                && launchSessionRole == PipelineRole.Actor
                && launchSessionStatus == SessionStatus.Ready)
                return true;

            return method == HttpMethods.Post
                && segments.Length == 6
                && segments[1] == "tasks"
                && GuidSegmentEquals(segments, 2, scopedTaskId)
                && segments[3] == "pipeline"
                && segments[4] == "review"
                && segments[5] == "start";
        }

        if (role == PipelineRole.Review)
        {
            if (IsReadOnlyProjectRequest(segments, projectId, taskId, method, includePipelineStatus: true))
                return true;

            return method == HttpMethods.Post
                && segments.Length == 5
                && segments[1] == "tasks"
                && GuidSegmentEquals(segments, 2, scopedTaskId)
                && segments[3] == "pipeline"
                && segments[4] is "verdict" or "approve";
        }

        return false;
    }

    private static bool IsReadOnlyProjectRequest(
        string[] segments,
        Guid projectId,
        Guid? taskId,
        string method,
        bool includePipelineStatus)
    {
        if (method != HttpMethods.Get)
            return false;

        if (segments.Length >= 3 && segments[1] == "projects"
            && !GuidSegmentEquals(segments, 2, projectId))
            return false;

        // Project, task list/detail, context, constraints, decisions, milestones, labels, reports.
        if (segments.Length == 3 && segments[1] == "projects" && GuidSegmentEquals(segments, 2, projectId))
            return true;
        if (segments.Length == 4 && segments[1] == "projects" && GuidSegmentEquals(segments, 2, projectId)
            && segments[3] is "tasks" or "constraints" or "decisions" or "milestones" or "labels" or "reports")
            return true;
        if (segments.Length == 5 && segments[1] == "projects" && GuidSegmentEquals(segments, 2, projectId)
            && segments[3] == "tasks" && Guid.TryParse(segments[4], out _))
            return true;
        if (segments.Length == 5 && segments[1] == "projects" && GuidSegmentEquals(segments, 2, projectId)
            && segments[3] == "context" && segments[4] == "nodes")
            return true;
        if (segments.Length == 6 && segments[1] == "projects" && GuidSegmentEquals(segments, 2, projectId)
            && segments[3] == "context" && segments[4] == "nodes" && Guid.TryParse(segments[5], out _))
            return true;
        if (segments.Length == 6 && segments[1] == "projects" && GuidSegmentEquals(segments, 2, projectId)
            && segments[3] == "context" && segments[4] == "export" && segments[5] == "agents-md")
            return true;
        if (segments.Length == 5 && segments[1] == "projects" && GuidSegmentEquals(segments, 2, projectId)
            && segments[3] == "labels" && segments[4] == "match")
            return true;
        if (segments.Length == 5 && segments[1] == "projects" && GuidSegmentEquals(segments, 2, projectId)
            && segments[3] == "reports" && Guid.TryParse(segments[4], out _))
            return true;

        // A task-scoped route must resolve to the task pinned to the eval or pipeline session.
        if (taskId is { } scopedTaskId && segments.Length == 3 && segments[1] == "tasks"
            && GuidSegmentEquals(segments, 2, scopedTaskId))
            return true;
        if (taskId is { } scopedTask && segments.Length == 4 && segments[1] == "tasks"
            && GuidSegmentEquals(segments, 2, scopedTask)
            && segments[3] == "steps")
            return true;
        if (includePipelineStatus && taskId is { } pipelineTask && segments.Length == 4
            && segments[1] == "tasks" && GuidSegmentEquals(segments, 2, pipelineTask)
            && segments[3] == "pipeline")
            return true;

        // Milestone details are tenant-scoped by RLS and used by Eval's get_milestone tool.
        return segments.Length == 3 && segments[1] == "milestones" && Guid.TryParse(segments[2], out _);
    }

    private static bool IsContextCompileRequest(string method, string? path)
    {
        var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return method == HttpMethods.Post
            && segments.Length == 5
            && segments[0] == "v1"
            && segments[1] == "projects"
            && Guid.TryParse(segments[2], out _)
            && segments[3] == "context"
            && segments[4] == "compile";
    }

    private static async Task<bool> IsContextCompileBodyScopedAsync(
        HttpRequest request,
        Guid? scopedTaskId,
        CancellationToken ct)
    {
        if (scopedTaskId is not { } expectedTaskId || expectedTaskId == Guid.Empty
            || request.ContentLength is > 64 * 1024)
            return false;

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && TryReadGuid(document.RootElement, "taskId", out var requestedTaskId)
                && requestedTaskId == expectedTaskId;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            if (request.Body.CanSeek)
                request.Body.Position = 0;
        }
    }

    internal static bool IsContextCompileBodyScoped(string payloadJson, Guid scopedTaskId)
    {
        if (scopedTaskId == Guid.Empty)
            return false;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && TryReadGuid(document.RootElement, "taskId", out var requestedTaskId)
                && requestedTaskId == scopedTaskId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsLaunchSessionRequest(string method, string? path)
    {
        var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return method == HttpMethods.Post
            && segments.Length == 4
            && segments[0] == "v1"
            && segments[1] == "sessions"
            && segments[3] == "launch";
    }

    private static bool TryReadGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out value)
            && value != Guid.Empty;
    }

    private static bool TryReadNullableGuid(JsonElement root, string name, out Guid? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String
            || !Guid.TryParse(element.GetString(), out var parsed)
            || parsed == Guid.Empty)
            return false;
        value = parsed;
        return true;
    }

    private static bool TryGetGuidSegment(string? path, int index, out Guid value)
    {
        value = Guid.Empty;
        var segments = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return index >= 0 && index < segments.Length && Guid.TryParse(segments[index], out value) && value != Guid.Empty;
    }

    private static bool GuidSegmentEquals(string[] segments, int index, Guid expected) =>
        expected != Guid.Empty && index >= 0 && index < segments.Length
        && Guid.TryParse(segments[index], out var actual) && actual == expected;

    private sealed record HarnessCommandScope(
        WorkstationCommandKind Kind,
        Guid ProjectId,
        Guid? TaskId,
        Guid? SessionId,
        PipelineRole? Role,
        Guid? SubtaskId);
}
