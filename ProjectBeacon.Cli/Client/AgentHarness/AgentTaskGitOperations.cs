namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Common;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Structured Git operations constrained to a daemon-created task worktree.
/// Repository, branch, base, and remote identity are derived from authorization and
/// the worktree metadata file; no tool accepts arbitrary refs or remote URLs.
/// </summary>
internal static class AgentTaskGitOperations
{
    private const int MaxOutputCharacters = 12_000;
    private const int MaxPathLength = 400;
    private const string IntegrationBranchName = "beacon/integration";

    private static readonly string[] GitHubCliEnvironmentNames =
    [
        "PATH", "SystemRoot", "WINDIR", "COMSPEC",
        "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "XDG_CONFIG_HOME", "GH_CONFIG_DIR",
        "GH_TOKEN", "GITHUB_TOKEN", "DBUS_SESSION_BUS_ADDRESS", "XDG_RUNTIME_DIR",
        "LANG", "LC_ALL"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record WorktreeMetadata(
        int Version,
        Guid ProjectId,
        Guid TaskId,
        string RepositoryRoot,
        string WorktreePath,
        string BranchName,
        string? BaseBranch,
        string BaseSha,
        DateTime CreatedAtUtc);

    internal sealed record GitContext(
        Guid TaskId,
        string RepositoryRoot,
        string WorktreeRoot,
        string ProjectRoot,
        string ProjectRelativePath,
        string BranchName,
        string? BaseBranch,
        string BaseSha,
        string MetadataPath,
        string[] DisabledFilters);

    private sealed record OperationState(bool Rebase, bool Merge, string? Error = null)
    {
        public bool Active => Rebase || Merge;
    }

    public static bool IsGitTool(string? name) =>
        name is PipelineToolPolicy.GitInfo
            or PipelineToolPolicy.GitCommit
            or PipelineToolPolicy.GitIntegrate
            or PipelineToolPolicy.GitPublish
            or PipelineToolPolicy.GitPullRequest;

    public static JsonObject ToolDefinition(string name) => name switch
    {
        PipelineToolPolicy.GitInfo => Definition(
            name,
            "Read the current task branch, its commits, diff, or file history. This tool is read-only and never accepts a remote or arbitrary revision.",
            new JsonObject
            {
                ["kind"] = EnumProperty("status", "log", "diff", "show", "blame", "branches"),
                ["path"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = MaxPathLength,
                    ["description"] = "Optional project-relative path for diff, show, or blame."
                },
                ["maxCount"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 40 }
            },
            "kind"),
        PipelineToolPolicy.GitCommit => Definition(
            name,
            "Create a durable commit on this task's own branch. The daemon supplies the author and stages only the authorized project directory.",
            new JsonObject
            {
                ["message"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 180 },
                ["paths"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = 64,
                    ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxPathLength },
                    ["description"] = "Optional project-relative paths. Omit to commit all changes under this project's directory."
                }
            },
            "message"),
        PipelineToolPolicy.GitIntegrate => Definition(
            name,
            "Refresh this task branch from its daemon-selected review base or the fixed beacon/integration branch, or continue/abort a reported conflict. source defaults to base; the agent cannot supply a remote or branch name.",
            new JsonObject
            {
                ["action"] = EnumProperty("integrate", "continue", "abort"),
                ["strategy"] = EnumProperty("rebase", "merge"),
                ["source"] = EnumProperty("base", "integration")
            },
            "action"),
        PipelineToolPolicy.GitPublish => Definition(
            name,
            "Publish only this task branch to its configured GitHub origin and create a pull request into the daemon-selected review base. Requires GitHub CLI (gh) authenticated on this workstation. Push is fast-forward-only; arbitrary remotes, target branches, force pushes, and merges are not allowed.",
            new JsonObject
            {
                ["action"] = EnumProperty("publish"),
                ["title"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 },
                ["body"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20_000 },
                ["draft"] = new JsonObject { ["type"] = "boolean", ["description"] = "Defaults to true." }
            },
            "action", "title"),
        PipelineToolPolicy.GitPullRequest => Definition(
            name,
            "Inspect the pull request belonging to this exact task branch. Any role may request status; only the Actor role may mark its own draft PR ready. Status reports exact task HEAD, pinned review-base freshness, GitHub review decision and required checks. This tool never merges; server-authorized GitLand remains the only landing path.",
            new JsonObject
            {
                ["action"] = EnumProperty("status", "ready")
            },
            "action"),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    public static async Task<McpToolResult> ExecuteAsync(
        AgentHarnessAuthorization authorization,
        string toolName,
        JsonObject arguments,
        CancellationToken ct)
    {
        if (!PipelineToolPolicy.GitToolsForRole(authorization.PipelineRole)
            .Contains(toolName, StringComparer.Ordinal))
            return Error("The current execution role is not authorized for this structured Git operation.");

        var opened = await OpenAsync(authorization, ct);
        if (opened.Context is null)
            return Error(opened.Error ?? "The task branch workspace is unavailable.");

        if (toolName == PipelineToolPolicy.GitIntegrate)
            return await IntegrateAsync(opened.Context, arguments, ct);
        if (toolName == PipelineToolPolicy.GitPublish)
            return await PublishAsync(opened.Context, arguments, ct);
        if (toolName == PipelineToolPolicy.GitPullRequest)
            return await AgentTaskGitHubPullRequestOperations.ExecuteAsync(
                opened.Context, authorization.PipelineRole, arguments, ct);

        return toolName switch
        {
            PipelineToolPolicy.GitInfo => await InfoAsync(opened.Context, arguments, ct),
            PipelineToolPolicy.GitCommit => await CommitAsync(opened.Context, authorization, arguments, ct),
            _ => Error("Unknown structured Git operation.")
        };
    }

    private static JsonObject Definition(
        string name,
        string description,
        JsonObject properties,
        params string[] required) => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                    ["additionalProperties"] = false
                }
            }
        };

    private static JsonObject EnumProperty(params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
    };

    private static async Task<(GitContext? Context, string? Error)> OpenAsync(
        AgentHarnessAuthorization authorization,
        CancellationToken ct)
    {
        if (!authorization.IsolatedWorkspace
            || authorization.TaskId is not { } taskId
            || taskId == Guid.Empty
            || authorization.GitWorkspace is not { } workspace)
            return (null, "Structured Git tools require a daemon-authorized persistent task branch workspace.");

        var expectedBranch = $"beacon/task/{taskId:N}";
        if (!string.Equals(workspace.BranchName, expectedBranch, StringComparison.Ordinal)
            || !IsGitSha(workspace.BaseSha)
            || string.IsNullOrWhiteSpace(workspace.RepositoryRoot)
            || string.IsNullOrWhiteSpace(workspace.WorktreeRoot))
            return (null, "The daemon supplied an invalid task branch identity.");

        try
        {
            var repositoryRoot = AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(workspace.RepositoryRoot));
            var worktreeRoot = AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(workspace.WorktreeRoot));
            var projectRoot = AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(authorization.WorkingDirectory));
            if (!Directory.Exists(repositoryRoot) || !Directory.Exists(worktreeRoot) || !Directory.Exists(projectRoot))
                return (null, "The task branch workspace directories are unavailable.");
            if (IsWithin(repositoryRoot, worktreeRoot) || !IsWithin(worktreeRoot, projectRoot))
                return (null, "The task branch workspace path is outside its authorized roots.");

            var top = await Git(worktreeRoot, [], ct, "rev-parse", "--show-toplevel");
            if (!Succeeded(top)
                || !SamePath(AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(top.StdOut.Trim())), worktreeRoot))
                return (null, "The authorized task directory is no longer the expected Git worktree.");

            var branch = await Git(worktreeRoot, [], ct, "symbolic-ref", "--quiet", "--short", "HEAD");
            if (!Succeeded(branch) || !string.Equals(branch.StdOut.Trim(), expectedBranch, StringComparison.Ordinal))
                return (null, "The task worktree is not checked out on its assigned task branch.");

            var metadataPath = worktreeRoot + ".beacon.json";
            if (IsReparsePoint(metadataPath))
                return (null, "Task worktree metadata cannot be a symbolic link or junction.");

            var metadata = JsonSerializer.Deserialize<WorktreeMetadata>(
                await File.ReadAllTextAsync(metadataPath, ct), JsonOptions);
            if (metadata is null
                || metadata.Version != 1
                || metadata.ProjectId != authorization.ProjectId
                || metadata.TaskId != taskId
                || !SamePath(AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(metadata.RepositoryRoot)), repositoryRoot)
                || !SamePath(AgentTaskWorktree.ResolvePhysicalPath(Path.GetFullPath(metadata.WorktreePath)), worktreeRoot)
                || !string.Equals(metadata.BranchName, expectedBranch, StringComparison.Ordinal)
                || !string.Equals(metadata.BaseBranch, workspace.BaseBranch, StringComparison.Ordinal)
                || !IsGitSha(metadata.BaseSha))
                return (null, "Task worktree metadata does not match the active command authorization.");

            var filterConfig = await Git(repositoryRoot, [], ct, "config", "--name-only", "--get-regexp", "^filter\\.");
            if (filterConfig.TimedOut || filterConfig.ExitCode is not (0 or 1))
                return (null, "Could not inspect Git filter configuration safely.");
            var filters = AgentSessionWorktree.ParseFilterDriverNames(filterConfig.StdOut);

            var projectRelative = Path.GetRelativePath(worktreeRoot, projectRoot).Replace('\\', '/');
            return (new GitContext(
                taskId,
                repositoryRoot,
                worktreeRoot,
                projectRoot,
                projectRelative == "." ? string.Empty : projectRelative,
                expectedBranch,
                metadata.BaseBranch,
                metadata.BaseSha,
                metadataPath,
                filters), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return (null, "Could not validate the task branch workspace (" + exception.GetType().Name + ").");
        }
    }

    private static async Task<McpToolResult> InfoAsync(GitContext context, JsonObject args, CancellationToken ct)
    {
        var kind = ReadString(args, "kind");
        if (kind is null)
            return Error("git_info requires kind.");

        var path = ReadOptionalPath(context, args, "path", out var pathError);
        if (pathError is not null)
            return Error(pathError);
        if ((kind is "show" or "blame") && path is null)
            return Error($"git_info kind={kind} requires a project-relative path.");

        switch (kind)
        {
            case "branches":
                {
                    var integration = await ReadIntegrationCheckpointAsync(context, ct);
                    if (integration.Error is not null)
                        return Error(integration.Error);
                    return Success(new
                    {
                        branch = context.BranchName,
                        baseBranch = context.BaseBranch,
                        baseSha = context.BaseSha,
                        integrationBranch = IntegrationBranchName,
                        integrationSha = integration.Sha
                    });
                }

            case "status":
                {
                    var statusArgs = new List<string> { "status", "--short", "--branch", "--untracked-files=all" };
                    AddProjectPathspec(context, statusArgs);
                    var status = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. statusArgs]);
                    var head = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", "HEAD");
                    if (!Succeeded(status) || !Succeeded(head))
                        return GitFailure("Could not read task branch status.");
                    var integration = await ReadIntegrationCheckpointAsync(context, ct);
                    if (integration.Error is not null)
                        return Error(integration.Error);
                    return Success(new
                    {
                        branch = context.BranchName,
                        baseBranch = context.BaseBranch,
                        baseSha = context.BaseSha,
                        integrationBranch = IntegrationBranchName,
                        integrationSha = integration.Sha,
                        head = head.StdOut.Trim(),
                        status = Clip(status.StdOut)
                    });
                }

            case "log":
                {
                    var maxCount = ReadInt(args, "maxCount") ?? 20;
                    if (maxCount is < 1 or > 40)
                        return Error("maxCount must be between 1 and 40.");
                    var logArgs = new List<string>
                    {
                        "log", "--oneline", "--decorate", "--no-color", "--max-count=" + maxCount,
                        $"{context.BaseSha}..HEAD"
                    };
                    AddProjectPathspec(context, logArgs, path);
                    var log = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. logArgs]);
                    return Succeeded(log) ? Success(new { commits = Clip(log.StdOut) }) : GitFailure("Could not read task branch history.");
                }

            case "diff":
                {
                    var diffArgs = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", context.BaseSha };
                    AddProjectPathspec(context, diffArgs, path);
                    var diff = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. diffArgs]);
                    return Succeeded(diff) ? Success(new { diff = Clip(diff.StdOut) }) : GitFailure("Could not read task branch diff.");
                }

            case "show":
                {
                    var showArgs = new List<string> { "show", "--no-ext-diff", "--no-color", "--format=fuller", "--stat", "HEAD" };
                    AddProjectPathspec(context, showArgs, path);
                    var show = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. showArgs]);
                    return Succeeded(show) ? Success(new { commit = Clip(show.StdOut) }) : GitFailure("Could not read task branch commit.");
                }

            case "blame":
                {
                    var blame = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                        "blame", "--no-ext-diff", "--no-color", "--", path!);
                    return Succeeded(blame) ? Success(new { path, blame = Clip(blame.StdOut) }) : GitFailure("Could not read file history.");
                }

            default:
                return Error("git_info kind must be one of: status, log, diff, show, blame, branches.");
        }
    }

    private static async Task<McpToolResult> CommitAsync(
        GitContext context,
        AgentHarnessAuthorization authorization,
        JsonObject args,
        CancellationToken ct)
    {
        var message = ReadString(args, "message")?.Trim();
        if (string.IsNullOrWhiteSpace(message) || message.Length > 180
            || message.Contains('\r') || message.Contains('\n'))
            return Error("git_commit message must be a single line of 1..180 characters.");

        var operation = await ReadOperationStateAsync(context, ct);
        if (operation.Error is not null)
            return Error("Cannot commit because the current Git operation state could not be verified. " + operation.Error);
        if (operation.Active)
            return Error("Cannot commit while a merge or rebase is in progress. Resolve it with git_integrate first.");

        var paths = ReadPathList(context, args, out var pathsError);
        if (pathsError is not null)
            return Error(pathsError);

        var addArgs = new List<string> { "add", "-A" };
        if (paths is { Count: > 0 })
        {
            addArgs.Add("--");
            addArgs.AddRange(paths.Select(Pathspec));
        }
        else
        {
            AddProjectPathspec(context, addArgs);
        }

        var add = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. addArgs]);
        if (!Succeeded(add))
            return GitFailure("Could not stage task branch changes.", add.StdErr);

        var summaryArgs = new List<string> { "diff", "--cached", "--summary", "HEAD" };
        if (paths is { Count: > 0 })
            summaryArgs.AddRange(["--", .. paths.Select(Pathspec)]);
        else
            AddProjectPathspec(context, summaryArgs);
        var summary = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. summaryArgs]);
        if (!Succeeded(summary))
            return GitFailure("Could not inspect staged task branch changes.", summary.StdErr);
        if (summary.StdOut.Contains("120000", StringComparison.Ordinal))
            return Error("Symbolic links cannot be committed through git_commit; staged changes remain in the task worktree.");

        var commitMessage = $"ProjectBeacon: task {context.TaskId:N}"
            + (authorization.SubtaskId is { } subtaskId ? $" subtask {subtaskId:N}" : string.Empty)
            + ": " + message;
        if (commitMessage.Length > 180)
            commitMessage = commitMessage[..180];

        // --only prevents an unrelated staged path elsewhere in a monorepo worktree from
        // being swept into a commit made for this project root.
        var commitArgs = new List<string>
        {
            "-c", "user.name=ProjectBeacon Agent",
            "-c", "user.email=projectbeacon-agent@localhost",
            "-c", "commit.gpgsign=false",
            "commit", "--no-verify", "--only", "-m", commitMessage
        };
        if (paths is { Count: > 0 })
        {
            commitArgs.Add("--");
            commitArgs.AddRange(paths.Select(Pathspec));
        }
        else
        {
            AddProjectPathspec(context, commitArgs);
        }

        var commit = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. commitArgs]);
        if (!Succeeded(commit))
            return GitFailure("Could not create the task branch commit. Ensure the selected paths contain changes.", commit.StdErr);

        var head = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", "HEAD");
        if (!Succeeded(head) || !IsGitSha(head.StdOut.Trim()))
            return GitFailure("The commit was created but its SHA could not be verified.");

        return Success(new { committed = true, commitSha = head.StdOut.Trim(), message });
    }

    private static async Task<McpToolResult> IntegrateAsync(GitContext context, JsonObject args, CancellationToken ct)
    {
        var allowedArguments = new HashSet<string>(StringComparer.Ordinal)
            { "action", "strategy", "source" };
        if (args.Any(pair => !allowedArguments.Contains(pair.Key)))
            return Error("git_integrate accepts only action, strategy, and source.");

        var action = ReadString(args, "action");
        if (action is not ("integrate" or "continue" or "abort"))
            return Error("git_integrate action must be integrate, continue, or abort.");

        if (action != "integrate" && (args.ContainsKey("source") || args.ContainsKey("strategy")))
            return Error("git_integrate strategy and source are available only with action=integrate.");

        if (action == "integrate")
        {
            var strategy = ReadString(args, "strategy");
            if (strategy is not ("rebase" or "merge"))
                return Error("git_integrate action=integrate requires strategy=rebase or strategy=merge.");
            var source = "base";
            if (args.ContainsKey("source"))
            {
                source = ReadString(args, "source");
                if (string.IsNullOrWhiteSpace(source))
                    return Error("git_integrate source must be base or integration.");
            }
            if (source is not ("base" or "integration"))
                return Error("git_integrate source must be base or integration.");

            var state = await ReadOperationStateAsync(context, ct);
            if (state.Error is not null)
                return Error("Cannot start integration because the current Git operation state could not be verified. " + state.Error);
            if (state.Active)
                return Error("A merge or rebase is already in progress. Use action=continue or action=abort.");

            var status = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(status))
                return GitFailure("Could not inspect task branch status before integration.");
            if (!string.IsNullOrWhiteSpace(status.StdOut))
                return Error("Commit or discard task worktree changes with git_commit before integrating a branch.");

            // A pending ref records the source SHA before Git mutates the task branch.
            // It is intentionally separate from the fetched remote ref: old fetches must
            // never be mistaken for an unfinished merge from a different source.
            var basePending = await ReadRefShaAsync(context, IntegrationBasePendingRef(context.TaskId), ct);
            var integrationPending = await ReadRefShaAsync(context, IntegrationPendingRef(context.TaskId), ct);
            if (basePending.Error is not null || integrationPending.Error is not null)
                return Error(basePending.Error ?? integrationPending.Error!);
            if (basePending.Sha is not null && integrationPending.Sha is not null)
                return Error("Both task integration sources have pending checkpoints. Resolve the existing Git state before starting another integration.");

            var pendingSource = basePending.Sha is not null ? "base"
                : integrationPending.Sha is not null ? "integration"
                : null;
            if (pendingSource is not null && !string.Equals(pendingSource, source, StringComparison.Ordinal))
                return Error($"A previous {pendingSource} integration is awaiting reconciliation. Retry git_integrate with source={pendingSource} before selecting another source.");

            var target = pendingSource == source
                ? (Sha: pendingSource == "base" ? basePending.Sha : integrationPending.Sha, Error: (string?)null)
                : source == "base"
                    ? await FetchBaseAsync(context, ct)
                    : await FetchIntegrationAsync(context, ct);
            if (target.Error is not null)
                return Error(target.Error);
            if (!IsGitSha(target.Sha ?? string.Empty))
                return Error("The selected integration source did not resolve to a valid commit SHA.");

            if (pendingSource is null)
            {
                var pendingRef = source == "base"
                    ? IntegrationBasePendingRef(context.TaskId)
                    : IntegrationPendingRef(context.TaskId);
                var pending = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                    "update-ref", pendingRef, target.Sha!);
                if (!Succeeded(pending))
                    return GitFailure("Could not persist the selected integration source checkpoint before merging.", pending.StdErr);
            }

            // A retry after a lost acknowledgement, or a task branch that already contains
            // the selected source, reconciles the checkpoint without creating a duplicate merge.
            var alreadyIntegrated = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                "merge-base", "--is-ancestor", target.Sha!, "HEAD");
            if (Succeeded(alreadyIntegrated))
            {
                var checkpointError = await CompleteIntegrationSourceAsync(context, source, target.Sha!, ct);
                if (checkpointError is not null)
                    return Error("The selected branch is already integrated, but its checkpoint could not be saved: " + checkpointError);
                return Success(new
                {
                    status = "up-to-date",
                    source,
                    strategy,
                    baseBranch = context.BaseBranch,
                    baseSha = source == "base" ? target.Sha : context.BaseSha,
                    integrationBranch = source == "integration" ? IntegrationBranchName : null,
                    integrationSha = source == "integration" ? target.Sha : null
                });
            }
            if (alreadyIntegrated.TimedOut || alreadyIntegrated.ExitCode != 1)
                return GitFailure("Could not verify whether the selected branch is already integrated.", alreadyIntegrated.StdErr);

            var integrate = strategy == "rebase"
                ? await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                    "-c", "core.editor=true", "-c", "sequence.editor=true", "rebase", target.Sha!)
                : await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                    "-c", "user.name=ProjectBeacon Agent",
                    "-c", "user.email=projectbeacon-agent@localhost",
                    "-c", "commit.gpgsign=false",
                    "-c", "core.editor=true", "merge", "--no-edit", target.Sha!);
            if (!Succeeded(integrate))
            {
                var operation = await ReadOperationStateAsync(context, ct);
                if (operation.Error is not null)
                    return Error("Git integration failed and its operation state could not be verified. The source checkpoint was retained. " + operation.Error);

                if (operation.Active)
                {
                    var mergeConflicts = await ReadConflictsAsync(context, ct);
                    if (mergeConflicts.Error is not null)
                        return Error("Git integration failed and unresolved conflict paths could not be verified. The source checkpoint was retained. " + mergeConflicts.Error);

                    return Success(new
                    {
                        status = "conflict",
                        source,
                        strategy,
                        targetSha = target.Sha,
                        files = mergeConflicts.Files,
                        error = mergeConflicts.Files.Count > 0
                            ? null
                            : "Git still has an active merge or rebase without unmerged paths. Continue or abort this saved integration before starting another operation."
                    });
                }

                _ = await ClearIntegrationPendingRefsAsync(context, CancellationToken.None);
                return GitFailure("Git could not integrate the selected branch. No resolvable conflict state was opened.", integrate.StdErr);
            }

            var update = await CompleteIntegrationSourceAsync(context, source, target.Sha!, ct);
            if (update is not null)
                return Error("The branch was integrated, but the saved source checkpoint could not be updated: " + update);
            return Success(new
            {
                status = "integrated",
                source,
                strategy,
                baseBranch = context.BaseBranch,
                baseSha = source == "base" ? target.Sha : context.BaseSha,
                integrationBranch = source == "integration" ? IntegrationBranchName : null,
                integrationSha = source == "integration" ? target.Sha : null
            });
        }

        var active = await ReadOperationStateAsync(context, ct);
        if (active.Error is not null)
            return Error("Cannot continue or abort because the current Git operation state could not be verified. " + active.Error);
        if (!active.Active)
            return Error("There is no merge or rebase in progress for this task branch.");

        if (action == "abort")
        {
            var abort = active.Rebase
                ? await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rebase", "--abort")
                : await Git(context.WorktreeRoot, context.DisabledFilters, ct, "merge", "--abort");
            if (!Succeeded(abort))
                return GitFailure("Could not abort the in-progress Git integration.");

            var clearError = await ClearIntegrationPendingRefsAsync(context, CancellationToken.None);
            return clearError is null
                ? Success(new { status = "aborted" })
                : Error("The Git operation was aborted, but its pending source checkpoint could not be cleared: " + clearError);
        }

        var conflictsResult = await ReadConflictsAsync(context, ct);
        if (conflictsResult.Error is not null)
            return Error("Cannot continue integration because unresolved conflict paths could not be verified. The source checkpoint was retained. " + conflictsResult.Error);
        var conflicts = conflictsResult.Files;
        if (conflicts.Any(path => !IsProjectRelativePath(context, path)))
            return Success(new
            {
                status = "conflict",
                files = conflicts,
                error = "Conflicts outside the authorized project directory cannot be resolved by this session."
            });

        if (conflicts.Count > 0)
        {
            var addArgs = new List<string> { "add", "-A", "--" };
            addArgs.AddRange(conflicts.Select(Pathspec));
            var add = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. addArgs]);
            if (!Succeeded(add))
                return GitFailure("Could not stage resolved conflict files.");
        }

        var remainingResult = await ReadConflictsAsync(context, ct);
        if (remainingResult.Error is not null)
            return Error("Could not verify unresolved conflict paths after staging. The source checkpoint was retained. " + remainingResult.Error);
        if (remainingResult.Files.Count > 0)
            return Success(new { status = "conflict", files = remainingResult.Files });

        var continuation = active.Rebase
            ? await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                "-c", "user.name=ProjectBeacon Agent",
                "-c", "user.email=projectbeacon-agent@localhost",
                "-c", "commit.gpgsign=false",
                "-c", "core.editor=true", "-c", "sequence.editor=true", "rebase", "--continue")
            : await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                "-c", "user.name=ProjectBeacon Agent",
                "-c", "user.email=projectbeacon-agent@localhost",
                "-c", "commit.gpgsign=false",
                "-c", "core.editor=true", "merge", "--continue");
        if (!Succeeded(continuation))
        {
            var stillActive = await ReadOperationStateAsync(context, ct);
            if (stillActive.Error is not null)
                return Error("Git could not continue the integration and its operation state could not be verified. The source checkpoint was retained. " + stillActive.Error);
            if (stillActive.Active)
            {
                var nextConflicts = await ReadConflictsAsync(context, ct);
                if (nextConflicts.Error is not null)
                    return Error("Git could not continue the integration and unresolved conflict paths could not be verified. The source checkpoint was retained. " + nextConflicts.Error);
                if (nextConflicts.Files.Count > 0)
                    return Success(new { status = "conflict", files = nextConflicts.Files });
            }
            return GitFailure("Git could not continue the integration.");
        }

        var basePendingAfter = await ReadRefShaAsync(context, IntegrationBasePendingRef(context.TaskId), ct);
        var integrationPendingAfter = await ReadRefShaAsync(context, IntegrationPendingRef(context.TaskId), ct);
        if (basePendingAfter.Error is not null || integrationPendingAfter.Error is not null)
            return Error(basePendingAfter.Error ?? integrationPendingAfter.Error!);
        if ((basePendingAfter.Sha is null) == (integrationPendingAfter.Sha is null))
            return Error("Could not identify a unique daemon-selected source for the completed integration.");

        var sourceName = integrationPendingAfter.Sha is not null ? "integration" : "base";
        var targetSha = integrationPendingAfter.Sha ?? basePendingAfter.Sha!;
        var metadataError = await CompleteIntegrationSourceAsync(context, sourceName, targetSha, ct);
        if (metadataError is not null)
            return Error("The integration finished, but the saved source checkpoint could not be updated: " + metadataError);

        return Success(new
        {
            status = "integrated",
            source = sourceName,
            baseBranch = context.BaseBranch,
            baseSha = sourceName == "base" ? targetSha : context.BaseSha,
            integrationBranch = sourceName == "integration" ? IntegrationBranchName : null,
            integrationSha = sourceName == "integration" ? targetSha : null
        });
    }

    private static async Task<(string? Sha, string? Error)> FetchBaseAsync(GitContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.BaseBranch))
            return (null, "This task branch has no recorded base branch and cannot be integrated.");

        var validate = await Git(context.RepositoryRoot, context.DisabledFilters, ct,
            "check-ref-format", "--branch", context.BaseBranch);
        if (!Succeeded(validate))
            return (null, "The recorded base branch is not a valid Git branch name.");

        var remote = await Git(context.RepositoryRoot, context.DisabledFilters, ct, "remote", "get-url", "origin");
        var targetRef = IntegrationBaseRef(context.TaskId);
        if (Succeeded(remote) && !string.IsNullOrWhiteSpace(remote.StdOut))
        {
            var url = remote.StdOut.Trim();
            if (!AgentTaskWorktree.IsSupportedFetchRemote(url))
                return (null, "The configured origin uses an unsupported Git transport; no integration was attempted.");

            if (IsSshRemote(url) && !HasHostSshAgent())
                return (null, "The origin uses SSH, but no host SSH agent is available. Start ssh-agent and load the GitHub key before integrating.");

            var fetch = await RunRemoteGitAsync(
                context.RepositoryRoot, context.DisabledFilters, ct, url,
                "fetch", "--no-tags", "--no-recurse-submodules", "--upload-pack=git-upload-pack",
                "--", url, $"+refs/heads/{context.BaseBranch}:{targetRef}");
            if (!Succeeded(fetch))
                return (null, "Could not fetch the recorded base branch. Check workstation connectivity and origin configuration.");
        }
        else
        {
            var local = await Git(context.RepositoryRoot, context.DisabledFilters, ct,
                "rev-parse", "--verify", $"refs/heads/{context.BaseBranch}");
            if (!Succeeded(local) || !IsGitSha(local.StdOut.Trim()))
                return (null, "No origin remote or local recorded base branch is available.");

            var updateLocalTarget = await Git(
                context.WorktreeRoot, context.DisabledFilters, ct,
                "update-ref", targetRef, local.StdOut.Trim());
            if (!Succeeded(updateLocalTarget))
                return (null, "Could not record the local base branch commit for integration.");
        }

        var head = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", targetRef);
        return Succeeded(head) && IsGitSha(head.StdOut.Trim())
            ? (head.StdOut.Trim(), null)
            : (null, "Could not verify the fetched base branch commit.");
    }

    private static async Task<(string? Sha, string? Error)> FetchIntegrationAsync(GitContext context, CancellationToken ct)
    {
        var validate = await Git(context.RepositoryRoot, context.DisabledFilters, ct,
            "check-ref-format", "--branch", IntegrationBranchName);
        if (!Succeeded(validate))
            return (null, "The fixed ProjectBeacon integration branch name is invalid.");

        var targetRef = IntegrationBranchRef(context.TaskId);
        var remotes = await Git(context.RepositoryRoot, context.DisabledFilters, ct, "remote");
        if (!Succeeded(remotes))
            return (null, "Could not inspect configured Git remotes.");

        var hasOrigin = remotes.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(name => string.Equals(name.Trim(), "origin", StringComparison.Ordinal));
        if (hasOrigin)
        {
            var remote = await Git(context.RepositoryRoot, context.DisabledFilters, ct,
                "remote", "get-url", "--all", "origin");
            if (!Succeeded(remote))
                return (null, "Could not resolve the configured origin URL safely.");
            var urls = remote.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
            if (urls.Length != 1)
                return (null, "Integration requires exactly one configured origin URL.");

            var url = urls[0];
            if (!AgentTaskWorktree.IsSupportedFetchRemote(url))
                return (null, "The configured origin uses an unsupported Git transport.");
            if (IsSshRemote(url) && !HasHostSshAgent())
                return (null, "The origin uses SSH, but no host SSH agent is available. Start ssh-agent and load the GitHub key before integrating.");

            var remoteBranch = await RunRemoteGitAsync(
                context.RepositoryRoot, context.DisabledFilters, ct, url,
                "ls-remote", "--heads", "--", url, $"refs/heads/{IntegrationBranchName}");
            if (!Succeeded(remoteBranch))
                return (null, "Could not inspect the remote integration branch.");
            var existsRemotely = remoteBranch.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.EndsWith($"\trefs/heads/{IntegrationBranchName}", StringComparison.Ordinal));
            if (existsRemotely)
            {
                var fetch = await RunRemoteGitAsync(
                    context.RepositoryRoot, context.DisabledFilters, ct, url,
                    "fetch", "--no-tags", "--no-recurse-submodules", "--upload-pack=git-upload-pack",
                    "--", url, $"+refs/heads/{IntegrationBranchName}:{targetRef}");
                if (!Succeeded(fetch))
                    return (null, "Could not fetch the remote integration branch. Check workstation connectivity and origin configuration.");
            }
            else
            {
                var local = await Git(context.RepositoryRoot, context.DisabledFilters, ct,
                    "rev-parse", "--verify", $"refs/heads/{IntegrationBranchName}");
                if (!Succeeded(local) || !IsGitSha(local.StdOut.Trim()))
                    return (null, "Neither origin nor the local repository contains the fixed integration branch.");
                var update = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                    "update-ref", targetRef, local.StdOut.Trim());
                if (!Succeeded(update))
                    return (null, "Could not record the local integration branch commit.");
            }
        }
        else
        {
            var local = await Git(context.RepositoryRoot, context.DisabledFilters, ct,
                "rev-parse", "--verify", $"refs/heads/{IntegrationBranchName}");
            if (!Succeeded(local) || !IsGitSha(local.StdOut.Trim()))
                return (null, "No origin or local ProjectBeacon integration branch is available.");
            var update = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                "update-ref", targetRef, local.StdOut.Trim());
            if (!Succeeded(update))
                return (null, "Could not record the local integration branch commit.");
        }

        var head = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", targetRef);
        return Succeeded(head) && IsGitSha(head.StdOut.Trim())
            ? (head.StdOut.Trim(), null)
            : (null, "Could not verify the fetched integration branch commit.");
    }

    private static async Task<string?> CompleteIntegrationSourceAsync(
        GitContext context, string source, string targetSha, CancellationToken ct)
    {
        if (!IsGitSha(targetSha))
            return "the integration source checkpoint is invalid";

        // Never advance a source checkpoint merely because an operation marker exists.
        // The exact fetched source commit must be reachable from the completed task HEAD.
        var included = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
            "merge-base", "--is-ancestor", targetSha, "HEAD");
        if (!Succeeded(included))
            return "the completed task branch does not contain the recorded integration source commit";

        if (source == "base")
        {
            var updateBase = await UpdateMetadataBaseShaAsync(context, targetSha, ct);
            if (updateBase is not null)
                return updateBase;
        }
        else if (source == "integration")
        {
            var checkpoint = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                "update-ref", IntegrationCheckpointRef(context.TaskId), targetSha);
            if (!Succeeded(checkpoint))
                return "the integration branch was merged but its checkpoint could not be persisted";
        }
        else
        {
            return "the integration source identity is invalid";
        }

        var pendingRef = source == "base"
            ? IntegrationBasePendingRef(context.TaskId)
            : IntegrationPendingRef(context.TaskId);
        var clearPending = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
            "update-ref", "-d", pendingRef);
        return Succeeded(clearPending)
            ? null
            : "the integration checkpoint was saved but its pending marker could not be cleared";
    }

    private static async Task<string?> ClearIntegrationPendingRefsAsync(
        GitContext context, CancellationToken ct)
    {
        foreach (var reference in new[]
        {
            IntegrationBasePendingRef(context.TaskId),
            IntegrationPendingRef(context.TaskId)
        })
        {
            var clear = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                "update-ref", "-d", reference);
            if (!Succeeded(clear) && clear.ExitCode != 1)
                return clear.StdErr.Trim().Length == 0
                    ? "a pending source ref could not be deleted"
                    : Clip(clear.StdErr.Trim());
        }

        return null;
    }

    private static async Task<(string? Sha, string? Error)> ReadRefShaAsync(
        GitContext context, string reference, CancellationToken ct)
    {
        var result = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
            "rev-parse", "--verify", reference);
        if (Succeeded(result) && IsGitSha(result.StdOut.Trim()))
            return (result.StdOut.Trim(), null);
        if (!result.TimedOut && result.ExitCode == 128)
            return (null, null);
        return (null, "Could not read a saved task integration checkpoint.");
    }

    private static async Task<(string? Sha, string? Error)> ReadIntegrationCheckpointAsync(
        GitContext context, CancellationToken ct) =>
        await ReadRefShaAsync(context, IntegrationCheckpointRef(context.TaskId), ct);

    private static string IntegrationBaseRef(Guid taskId) => $"refs/beacon/task/{taskId:N}/integration-base";
    private static string IntegrationBranchRef(Guid taskId) => $"refs/beacon/task/{taskId:N}/integration-branch";
    private static string IntegrationBasePendingRef(Guid taskId) => $"refs/beacon/task/{taskId:N}/integration-base-pending";
    private static string IntegrationPendingRef(Guid taskId) => $"refs/beacon/task/{taskId:N}/integration-pending";
    private static string IntegrationCheckpointRef(Guid taskId) => $"refs/beacon/task/{taskId:N}/integration-checkpoint";

    internal sealed record PullRequestInfo(int Number, string Url, string BaseRefName, string State, bool IsDraft);

    private static async Task<McpToolResult> PublishAsync(
        GitContext context,
        JsonObject args,
        CancellationToken ct)
    {
        var allowedArguments = new HashSet<string>(StringComparer.Ordinal)
            { "action", "title", "body", "draft" };
        if (args.Any(pair => !allowedArguments.Contains(pair.Key)))
            return Error("git_publish accepts only action, title, body, and draft.");
        if (!string.Equals(ReadString(args, "action"), "publish", StringComparison.Ordinal))
            return Error("git_publish action must be publish.");

        var title = ReadString(args, "title").Trim();
        var body = ReadString(args, "body");
        var draft = args["draft"] is JsonValue draftValue && draftValue.TryGetValue<bool>(out var requestedDraft)
            ? requestedDraft
            : true;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            return Error("git_publish requires a title of 1..200 characters.");
        if (body.Length > 20_000)
            return Error("git_publish body is limited to 20,000 characters.");
        if (context.BaseBranch is not { Length: > 0 } baseBranch
            || !IsGitSha(context.BaseSha)
            || string.Equals(baseBranch, context.BranchName, StringComparison.Ordinal))
            return Error("The task branch has no valid daemon-selected review base.");

        var branchCheck = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
            "check-ref-format", "--branch", baseBranch);
        if (!Succeeded(branchCheck))
            return Error("The daemon-selected review base is not a valid Git branch.");

        var operation = await ReadOperationStateAsync(context, ct);
        if (operation.Error is not null)
            return Error("Publishing is refused because the current Git operation state could not be verified. " + operation.Error);
        if (operation.Active)
            return Error("Resolve the active merge or rebase with git_integrate before publishing.");

        var origin = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "remote", "get-url", "--all", "origin");
        if (!Succeeded(origin))
            return Error("Could not inspect the configured origin fetch URLs.");
        var originUrls = origin.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (originUrls.Length != 1)
            return Error("Publishing requires exactly one credential-free HTTPS or SSH origin URL for github.com/owner/repository.");
        var originUrl = originUrls[0].Trim();
        if (!TryParseGitHubRemote(originUrl, out var repository))
            return Error("Publishing requires exactly one credential-free HTTPS or SSH origin URL for github.com/owner/repository.");

        var pushOrigin = await Git(
            context.WorktreeRoot, context.DisabledFilters, ct, "remote", "get-url", "--push", "--all", "origin");
        if (!Succeeded(pushOrigin))
            return Error("Could not inspect the configured origin push URLs.");
        var pushUrls = pushOrigin.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (pushUrls.Length != 1)
            return Error("The origin push URL must be a single GitHub URL for the same repository as the validated origin fetch URL.");
        var pushUrl = pushUrls[0].Trim();
        if (!TryParseGitHubRemote(pushUrl, out var pushRepository)
            || !string.Equals(repository, pushRepository, StringComparison.OrdinalIgnoreCase))
            return Error("The origin push URL must be a single GitHub URL for the same repository as the validated origin fetch URL.");

        var mirror = await Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "config", "--bool", "--get", "remote.origin.mirror");
        if (mirror.TimedOut || mirror.ExitCode is not (0 or 1))
            return Error("Could not verify that origin is not configured for mirror pushes.");
        if (mirror.ExitCode == 0)
        {
            if (!bool.TryParse(mirror.StdOut.Trim(), out var mirrorEnabled))
                return Error("The origin mirror configuration is invalid; publishing is refused.");
            if (mirrorEnabled)
                return Error("Publishing refuses remote.origin.mirror=true because it can push refs beyond the authorized task branch.");
        }

        var status = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
            "status", "--porcelain", "--untracked-files=all");
        if (!Succeeded(status))
            return GitFailure("Could not inspect the task worktree before publishing.", status.StdErr);
        if (!string.IsNullOrWhiteSpace(status.StdOut))
            return Error("The task worktree is not clean. Commit the task changes with git_commit before publishing; no files were pushed.");

        var head = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", "HEAD");
        if (!Succeeded(head) || !IsGitSha(head.StdOut.Trim()))
            return GitFailure("Could not resolve the task branch HEAD before publishing.", head.StdErr);
        var headSha = head.StdOut.Trim();

        var baseIsAncestor = await Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "merge-base", "--is-ancestor", context.BaseSha, headSha);
        if (!Succeeded(baseIsAncestor))
            return Error("The task branch no longer descends from its pinned review base. Run git_integrate and review it again before publishing.");

        var changedPaths = await Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z",
            context.BaseSha, headSha);
        if (!Succeeded(changedPaths))
            return GitFailure("Could not verify the task branch change scope before publishing.", changedPaths.StdErr);
        var changedPathList = changedPaths.StdOut
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (changedPathList.Length == 0)
            return Error("The task branch has no file changes to publish.");
        var changeSummary = await Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "diff", "--no-ext-diff", "--no-textconv", "--summary",
            context.BaseSha, headSha);
        if (!Succeeded(changeSummary))
            return GitFailure("Could not inspect file modes before publishing.", changeSummary.StdErr);
        if (changeSummary.StdOut.Contains("120000", StringComparison.Ordinal)
            || changeSummary.StdOut.Contains("160000", StringComparison.Ordinal))
            return Error("Publishing symlink or submodule changes is not supported by the task Git workflow.");
        var outsidePaths = changedPathList
            .Where(path => !IsProjectRelativePath(context, path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (outsidePaths.Length > 0)
            return Error(
                "The task branch contains committed changes outside the authorized project directory. "
                + "Publishing was refused: " + string.Join(", ", outsidePaths.Take(8)));

        // The pinned-base ancestry check and the non-empty tree diff above establish
        // that this branch contains publishable task changes. A separate rev-list count
        // adds no safety here and can fail independently of the already-validated diff.

        var safePath = NativeCommandRunner.BuildSafePath(
            Environment.GetEnvironmentVariable("PATH"), context.WorktreeRoot);
        var ghPath = WorkstationProbe.Which("gh", safePath);
        if (ghPath is null)
            return Error("GitHub publishing requires GitHub CLI (gh). Install it and run gh auth login on this workstation.");
        var auth = await RunGitHubCliAsync(ghPath, context.WorktreeRoot, ct, "auth", "status", "--hostname", "github.com");
        if (auth.ExitCode != 0 || auth.TimedOut)
            return Error("GitHub CLI is not authenticated for github.com. Run gh auth login on this workstation; no credential was requested or stored by ProjectBeacon.");

        // Git runs with an isolated HOME/APPDATA. Point only the bounded credential-helper
        // subprocesses back at the host gh config and keyring session; never copy a token
        // into persistent Git configuration or command arguments.
        if ((IsSshRemote(originUrl) || IsSshRemote(pushUrl)) && !HasHostSshAgent())
            return Error("The configured GitHub SSH remote requires an active host SSH agent. Start ssh-agent and load the GitHub key, or use an authenticated HTTPS origin.");

        var existing = await ListPullRequestsAsync(ghPath, context.WorktreeRoot, repository, context.BranchName, ct);
        if (existing.Error is not null)
            return Error(existing.Error);
        var matchingBase = existing.PullRequests.FirstOrDefault(
            pull => string.Equals(pull.BaseRefName, baseBranch, StringComparison.Ordinal));
        var differentBase = existing.PullRequests.FirstOrDefault(
            pull => !string.Equals(pull.BaseRefName, baseBranch, StringComparison.Ordinal));
        if (differentBase is not null)
            return Error(
                $"This task branch already has pull request #{differentBase.Number} targeting '{differentBase.BaseRefName}' ({differentBase.Url}). "
                + $"The daemon-selected review base is '{baseBranch}'; retarget that PR explicitly before publishing again.");
        if (matchingBase is { State: "MERGED" } merged)
        {
            var mergedSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
                ghPath, context.WorktreeRoot, repository, merged.Number, ct);
            if (mergedSnapshot.Error is not null)
                return Error("The task PR is merged, but its exact merged head could not be verified. " + mergedSnapshot.Error);
            var mergedView = mergedSnapshot.View!;
            if (!AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
                    mergedView, "MERGED", baseBranch, headSha))
                return Error(
                    $"Task branch {context.BranchName} has a merged PR #{merged.Number}, but that PR does not contain the current task HEAD {headSha}. "
                    + "The current task changes have not been confirmed as merged; create a new task branch for additional work.");

            return Success(new
            {
                published = true,
                alreadyMerged = true,
                repository,
                branch = context.BranchName,
                baseBranch,
                headSha,
                pullRequestNumber = merged.Number,
                pullRequestUrl = mergedView.Url,
                draft = false,
                mergedAt = mergedView.MergedAt,
                mergeCommitSha = mergedView.MergeCommitSha
            });
        }
        if (matchingBase is { State: "CLOSED" } closed)
            return Error(
                $"Pull request #{closed.Number} for this task branch is already closed ({closed.Url}). "
                + "Reopen it explicitly or publish a new task branch; ProjectBeacon will not silently recreate it.");

        // Use the exact URL validated above rather than the mutable origin alias.
        var baseOnRemote = await RunRemoteGitAsync(
            context.WorktreeRoot, context.DisabledFilters, ct, originUrl,
            "ls-remote", "--heads", originUrl, "refs/heads/" + baseBranch);
        if (!Succeeded(baseOnRemote))
            return GitFailure("Could not verify the review base on the configured GitHub origin.", baseOnRemote.StdErr);
        if (string.IsNullOrWhiteSpace(baseOnRemote.StdOut))
            return Error($"The configured GitHub origin does not contain the review base branch '{baseBranch}'.");
        var remoteBaseLine = baseOnRemote.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.EndsWith("\trefs/heads/" + baseBranch, StringComparison.Ordinal));
        var remoteBaseSha = remoteBaseLine?.Split('\t').FirstOrDefault();
        if (!IsGitSha(remoteBaseSha ?? string.Empty)
            || !string.Equals(remoteBaseSha, context.BaseSha, StringComparison.OrdinalIgnoreCase))
            return Error("The remote review base has advanced since this task branch was last integrated. Run git_integrate, review the updated branch, and retry publishing.");

        // Push the already-verified commit SHA directly to the one validated URL and
        // the task's single fixed destination ref. No remote alias, configured pushurl,
        // mirror setting, or local branch movement can broaden or redirect this push.
        var push = await RunRemoteGitAsync(
            context.WorktreeRoot, context.DisabledFilters, ct, pushUrl,
            "push", "--no-follow-tags", "--porcelain", pushUrl,
            $"{headSha}:refs/heads/{context.BranchName}");
        if (!Succeeded(push))
            return GitFailure(
                "Could not publish the task branch. The push is non-forced; resolve any remote divergence and retry.",
                push.StdErr);

        if (matchingBase is { State: "OPEN" } open)
        {
            var openSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
                ghPath, context.WorktreeRoot, repository, open.Number, ct);
            if (openSnapshot.Error is not null)
                return Error("The task branch was pushed, but its existing pull request could not be verified. " + openSnapshot.Error);
            var openView = openSnapshot.View!;
            if (!AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
                    openView, "OPEN", baseBranch, headSha))
                return Error(
                    $"The task branch was pushed at {headSha}, but existing PR #{open.Number} does not show that exact HEAD and base. "
                    + "Inspect git_pr status before proceeding.");

            return Success(new
            {
                published = true,
                pullRequestCreated = false,
                repository,
                branch = context.BranchName,
                baseBranch,
                headSha,
                pullRequestNumber = open.Number,
                pullRequestUrl = openView.Url,
                draft = openView.IsDraft
            });
        }

        var createArguments = new List<string>
        {
            "pr", "create", "--repo", repository,
            "--head", repository.Split('/')[0] + ":" + context.BranchName,
            "--base", baseBranch,
            "--title", title,
            "--body", body
        };
        if (draft)
            createArguments.Add("--draft");

        var created = await RunGitHubCliAsync(ghPath, context.WorktreeRoot, ct, [.. createArguments]);
        if (created.ExitCode != 0 || created.TimedOut)
        {
            // A concurrent publisher may have created the PR between the list and create calls.
            var refreshed = await ListPullRequestsAsync(ghPath, context.WorktreeRoot, repository, context.BranchName, ct);
            var raced = refreshed.PullRequests.FirstOrDefault(
                pull => string.Equals(pull.BaseRefName, baseBranch, StringComparison.Ordinal)
                    && string.Equals(pull.State, "OPEN", StringComparison.Ordinal));
            if (raced is not null)
            {
                var racedSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
                    ghPath, context.WorktreeRoot, repository, raced.Number, ct);
                if (racedSnapshot.Error is null
                    && racedSnapshot.View is { } racedView
                    && AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
                        racedView, "OPEN", baseBranch, headSha))
                    return Success(new
                    {
                        published = true,
                        pullRequestCreated = false,
                        repository,
                        branch = context.BranchName,
                        baseBranch,
                        headSha,
                        pullRequestNumber = raced.Number,
                        pullRequestUrl = racedView.Url,
                        draft = racedView.IsDraft
                    });
            }
            return Error(
                "The task branch was pushed, but GitHub did not create the pull request. "
                + "Check GitHub CLI permissions and retry. " + Clip(created.StdErr.Trim()));
        }

        var pullRequestUrl = created.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase));
        if (pullRequestUrl is null)
            return Error("The task branch was pushed, but GitHub CLI did not return a pull request URL. Check the repository pull request state before retrying.");

        var pullRequestNumberText = pullRequestUrl.TrimEnd('/').Split('/').LastOrDefault();
        if (!int.TryParse(pullRequestNumberText, NumberStyles.None, CultureInfo.InvariantCulture, out var pullRequestNumber)
            || pullRequestNumber <= 0)
            return Error("The task branch was pushed and GitHub returned a PR URL, but its pull request number could not be parsed safely: " + pullRequestUrl);

        var createdSnapshot = await AgentTaskGitHubPullRequestOperations.ReadSnapshotAsync(
            ghPath, context.WorktreeRoot, repository, pullRequestNumber, ct);
        if (createdSnapshot.Error is not null)
            return Error("The task branch was pushed and its PR was created, but the resulting PR state could not be verified. " + createdSnapshot.Error);
        var createdView = createdSnapshot.View!;
        if (!AgentTaskGitHubPullRequestOperations.MatchesPublishedPullRequest(
                createdView, "OPEN", baseBranch, headSha)
            || createdView.IsDraft != draft)
            return Error(
                "The task branch was pushed and its PR was created, but GitHub returned a different head, base, or draft state. Inspect git_pr status before proceeding.");

        return Success(new
        {
            published = true,
            pullRequestCreated = true,
            repository,
            branch = context.BranchName,
            baseBranch,
            headSha,
            pullRequestNumber,
            pullRequestUrl = createdView.Url,
            draft = createdView.IsDraft
        });
    }

    internal static string[] BuildListPullRequestsArguments(string repository, string branch) =>
    [
        "pr", "list", "--repo", repository,
        "--head", branch,
        "--state", "all",
        "--json", "number,url,baseRefName,state,isDraft"
    ];

    internal static async Task<(IReadOnlyList<PullRequestInfo> PullRequests, string? Error)> ListPullRequestsAsync(
        string ghPath,
        string workingDirectory,
        string repository,
        string branch,
        CancellationToken ct)
    {
        var result = await RunGitHubCliAsync(
            ghPath, workingDirectory, ct,
            BuildListPullRequestsArguments(repository, branch));
        if (result.ExitCode != 0 || result.TimedOut)
            return ([], "Could not inspect existing GitHub pull requests. Check GitHub CLI authentication and repository access.");
        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return ([], "GitHub CLI returned an invalid pull request list.");
            var items = new List<PullRequestInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("number", out var numberValue)
                    || !numberValue.TryGetInt32(out var number)
                    || !TryJsonString(item, "url", out var url)
                    || !TryJsonString(item, "baseRefName", out var baseName)
                    || !TryJsonString(item, "state", out var state)
                    || !item.TryGetProperty("isDraft", out var draftValue)
                    || draftValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return ([], "GitHub CLI returned incomplete pull request metadata.");
                items.Add(new PullRequestInfo(number, url, baseName, state.ToUpperInvariant(), draftValue.GetBoolean()));
            }
            return (items, null);
        }
        catch (JsonException)
        {
            return ([], "GitHub CLI returned malformed pull request metadata.");
        }
    }

    internal static async Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunGitHubCliAsync(
        string ghPath,
        string workingDirectory,
        CancellationToken ct,
        params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = ghPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var safePath = NativeCommandRunner.BuildSafePath(
            Environment.GetEnvironmentVariable("PATH"), workingDirectory);
        var inherited = BuildGitHubCliEnvironment(Environment.GetEnvironmentVariable);
        info.Environment.Clear();
        foreach (var pair in inherited)
        {
            if (!string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase))
                info.Environment[pair.Key] = pair.Value;
        }
        info.Environment["PATH"] = safePath;
        if (OperatingSystem.IsWindows())
        {
            var trustedCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            info.Environment["COMSPEC"] = trustedCmd;
        }
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(90), ct);
    }

    internal static IReadOnlyDictionary<string, string> BuildGitHubCliEnvironment(
        Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(comparer);
        foreach (var pair in ClientConfiguration.CaptureAllowedEnvironment(GitHubCliEnvironmentNames, lookup))
            environment[pair.Key] = pair.Value;
        environment["GH_PROMPT_DISABLED"] = "1";
        environment["GH_PAGER"] = "cat";
        environment["PAGER"] = "cat";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        return environment;
    }

    private static IReadOnlyDictionary<string, string>? BuildHostGitHubCredentialEnvironment()
    {
        var isWindows = OperatingSystem.IsWindows();
        var homeDirectory = isWindows
            ? Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return BuildGitHubCredentialEnvironment(
            Environment.GetEnvironmentVariable("GH_CONFIG_DIR"),
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetEnvironmentVariable("APPDATA"),
            homeDirectory,
            Environment.GetEnvironmentVariable("GH_TOKEN"),
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
            Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"),
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            isWindows);
    }

    private static IReadOnlyDictionary<string, string>? BuildHostSshAgentEnvironment()
    {
        var isWindows = OperatingSystem.IsWindows();
        var socket = ResolveSshAgentSocket(
            Environment.GetEnvironmentVariable("SSH_AUTH_SOCK"),
            isWindows);
        var environment = BuildSshAgentEnvironment(
                socket,
                Environment.GetEnvironmentVariable("SSH_AGENT_PID"))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var homeDirectory = isWindows
            ? Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var knownHostsPath = ResolveHostKnownHostsPath(homeDirectory);
        environment["GIT_SSH_COMMAND"] = BuildSshCommand(knownHostsPath, isWindows);
        return environment.Count > 0 ? environment : null;
    }

    internal static string? ResolveSshAgentSocket(string? sshAgentSocket, bool isWindows)
        => !string.IsNullOrWhiteSpace(sshAgentSocket)
            ? sshAgentSocket
            : isWindows ? @"\\.\pipe\openssh-ssh-agent" : null;

    internal static string? ResolveHostKnownHostsPath(string? homeDirectory)
        => string.IsNullOrWhiteSpace(homeDirectory)
            ? null
            : Path.Combine(homeDirectory, ".ssh", "known_hosts");

    internal static string BuildSshCommand(string? knownHostsPath, bool isWindows)
    {
        const string baseCommand = "ssh -o BatchMode=yes -o StrictHostKeyChecking=yes -o UpdateHostKeys=no";
        if (string.IsNullOrWhiteSpace(knownHostsPath))
            return baseCommand;

        var normalizedPath = isWindows ? knownHostsPath.Replace('\\', '/') : knownHostsPath;
        var quotedPath = "'" + normalizedPath.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        return baseCommand + " -o UserKnownHostsFile=" + quotedPath;
    }

    private static bool HasHostSshAgent()
        => ResolveSshAgentSocket(
            Environment.GetEnvironmentVariable("SSH_AUTH_SOCK"),
            OperatingSystem.IsWindows()) is not null;

    private static IReadOnlyDictionary<string, string>? BuildGitRemoteCredentialEnvironment(string remoteUrl)
    {
        if (IsGitHubHttpsRemote(remoteUrl))
            return BuildHostGitHubCredentialEnvironment();
        if (IsSshRemote(remoteUrl))
            return BuildHostSshAgentEnvironment();
        return null;
    }

    internal static Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunRemoteGitAsync(
        string directory,
        IReadOnlyCollection<string> disabledFilters,
        CancellationToken ct,
        string remoteUrl,
        params string[] arguments)
    {
        var environment = BuildGitRemoteCredentialEnvironment(remoteUrl);
        var commandArguments = new List<string>(arguments.Length + 2);
        if (IsGitHubHttpsRemote(remoteUrl) && environment is not null)
            commandArguments.AddRange(["-c", "credential.helper=!gh auth git-credential"]);
        commandArguments.AddRange(arguments);

        return environment is null
            ? Git(directory, disabledFilters, ct, [.. commandArguments])
            : AgentSessionWorktree.RunGitWithEnvironmentAsync(
                directory, ct, disabledFilters, environment, [.. commandArguments]);
    }

    internal static IReadOnlyDictionary<string, string> BuildSshAgentEnvironment(
        string? sshAgentSocket,
        string? sshAgentPid)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(sshAgentSocket))
            environment["SSH_AUTH_SOCK"] = sshAgentSocket;
        if (!string.IsNullOrWhiteSpace(sshAgentPid))
            environment["SSH_AGENT_PID"] = sshAgentPid;
        return environment;
    }

    internal static bool IsGitHubHttpsRemote(string remoteUrl)
        => TryParseGitHubRemote(remoteUrl, out _)
            && Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSshRemote(string remoteUrl)
    {
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            return true;

        var value = remoteUrl.Trim();
        var colon = value.IndexOf(':');
        var separator = value.IndexOfAny(['/', '\\']);
        return colon > 0
            && (separator < 0 || colon < separator)
            && value[..colon].Contains('@');
    }

    internal static IReadOnlyDictionary<string, string>? BuildGitHubCredentialEnvironment(
        string? configuredDirectory,
        string? xdgConfigHome,
        string? appDataDirectory,
        string? homeDirectory,
        string? ghToken,
        string? githubToken,
        string? dbusSessionBusAddress,
        string? xdgRuntimeDirectory,
        bool isWindows)
    {
        var configDirectory = ResolveGitHubConfigDirectory(
            configuredDirectory, xdgConfigHome, appDataDirectory, homeDirectory, isWindows);
        if (string.IsNullOrWhiteSpace(configDirectory)
            && string.IsNullOrWhiteSpace(ghToken)
            && string.IsNullOrWhiteSpace(githubToken))
            return null;

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GH_PROMPT_DISABLED"] = "1"
        };
        if (!string.IsNullOrWhiteSpace(configDirectory))
            environment["GH_CONFIG_DIR"] = configDirectory;
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            environment["HOME"] = homeDirectory;
            if (isWindows)
                environment["USERPROFILE"] = homeDirectory;
        }
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            environment["XDG_CONFIG_HOME"] = xdgConfigHome;
        if (isWindows && !string.IsNullOrWhiteSpace(appDataDirectory))
            environment["APPDATA"] = appDataDirectory;
        if (!string.IsNullOrWhiteSpace(ghToken))
            environment["GH_TOKEN"] = ghToken;
        if (!string.IsNullOrWhiteSpace(githubToken))
            environment["GITHUB_TOKEN"] = githubToken;
        if (!string.IsNullOrWhiteSpace(dbusSessionBusAddress))
            environment["DBUS_SESSION_BUS_ADDRESS"] = dbusSessionBusAddress;
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            environment["XDG_RUNTIME_DIR"] = xdgRuntimeDirectory;
        return environment;
    }

    internal static string? ResolveGitHubConfigDirectory(
        string? configuredDirectory,
        string? xdgConfigHome,
        string? appDataDirectory,
        string? homeDirectory,
        bool isWindows)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            return configuredDirectory;
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
            return Path.Combine(xdgConfigHome, "gh");
        if (isWindows && !string.IsNullOrWhiteSpace(appDataDirectory))
            return Path.Combine(appDataDirectory, "GitHub CLI");
        return !string.IsNullOrWhiteSpace(homeDirectory)
            ? Path.Combine(homeDirectory, ".config", "gh")
            : null;
    }

    internal static bool TryParseGitHubRemote(string remoteUrl, out string repository)
    {
        repository = string.Empty;
        if (string.IsNullOrWhiteSpace(remoteUrl) || remoteUrl.Contains('\0'))
            return false;
        var value = remoteUrl.Trim();
        string path;
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = value["git@github.com:".Length..];
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return false;
            if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
                    return false;
            }
            else if (uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(uri.UserInfo, "git", StringComparison.Ordinal)
                    || (uri.Port != 22 && !uri.IsDefaultPort))
                    return false;
            }
            else
            {
                return false;
            }
            path = uri.AbsolutePath.Trim('/');
        }
        else
        {
            return false;
        }

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        var parts = path.Split('/');
        if (parts.Length != 2 || !IsSafeGitHubPathSegment(parts[0]) || !IsSafeGitHubPathSegment(parts[1]))
            return false;
        repository = parts[0] + "/" + parts[1];
        return true;
    }

    private static bool IsSafeGitHubPathSegment(string value) =>
        value.Length is > 0 and <= 100
        && value is not ("." or "..")
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool TryJsonString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    internal static string? GetOperationStateReadError(
        (int ExitCode, string StdOut, string StdErr, bool TimedOut) rebaseMerge,
        (int ExitCode, string StdOut, string StdErr, bool TimedOut) rebaseApply,
        (int ExitCode, string StdOut, string StdErr, bool TimedOut) mergeHead)
    {
        static bool IsReadable((int ExitCode, string StdOut, string StdErr, bool TimedOut) result) =>
            Succeeded(result) && !string.IsNullOrWhiteSpace(result.StdOut);

        return IsReadable(rebaseMerge) && IsReadable(rebaseApply) && IsReadable(mergeHead)
            ? null
            : "Git did not return all merge/rebase state markers successfully.";
    }

    private static async Task<OperationState> ReadOperationStateAsync(GitContext context, CancellationToken ct)
    {
        var rebaseMerge = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--git-path", "rebase-merge");
        var rebaseApply = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--git-path", "rebase-apply");
        var mergeHead = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--git-path", "MERGE_HEAD");

        var error = GetOperationStateReadError(rebaseMerge, rebaseApply, mergeHead);
        if (error is not null)
            return new OperationState(false, false, error);

        bool Exists((int ExitCode, string StdOut, string StdErr, bool TimedOut) result)
        {
            var path = result.StdOut.Trim();
            if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(context.WorktreeRoot, path));

            try
            {
                _ = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        return new OperationState(
            Exists(rebaseMerge) || Exists(rebaseApply),
            Exists(mergeHead));
    }

    private static async Task<(List<string> Files, string? Error)> ReadConflictsAsync(
        GitContext context, CancellationToken ct)
    {
        var result = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "ls-files", "-u", "-z");
        return ParseConflictListResult(result);
    }

    internal static (List<string> Files, string? Error) ParseConflictListResult(
        (int ExitCode, string StdOut, string StdErr, bool TimedOut) result)
    {
        if (!Succeeded(result))
            return ([], "Git did not successfully enumerate unresolved conflict paths.");
        if (result.StdOut.Length == 0)
            return ([], null);
        if (result.StdOut[^1] != '\0')
            return ([], "Git returned an incomplete unresolved-conflict listing.");

        var entries = result.StdOut.Split('\0', StringSplitOptions.None);
        if (entries.Length < 2 || entries[^1].Length != 0)
            return ([], "Git returned an invalid unresolved-conflict listing.");

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries[..^1])
        {
            var tab = entry.IndexOf('\t');
            if (tab <= 0 || tab == entry.Length - 1)
                return ([], "Git returned a malformed unresolved-conflict record.");

            var header = entry[..tab].Split(' ', StringSplitOptions.None);
            if (header.Length != 3
                || header[0].Length != 6
                || !header[0].All(character => character is >= '0' and <= '7')
                || !IsGitSha(header[1])
                || header[2] is not ("1" or "2" or "3"))
                return ([], "Git returned a malformed unresolved-conflict record.");

            var path = entry[(tab + 1)..];
            if (path.Length == 0)
                return ([], "Git returned an unresolved-conflict record without a path.");
            if (seen.Add(path))
                files.Add(path);
        }

        return (files, null);
    }

    private static async Task<string?> UpdateMetadataBaseShaAsync(
        GitContext context,
        string baseSha,
        CancellationToken ct)
    {
        try
        {
            var current = JsonSerializer.Deserialize<WorktreeMetadata>(
                await File.ReadAllTextAsync(context.MetadataPath, ct), JsonOptions);
            if (current is null
                || current.ProjectId == Guid.Empty
                || current.TaskId != context.TaskId
                || !string.Equals(current.BranchName, context.BranchName, StringComparison.Ordinal)
                || !IsGitSha(baseSha))
                return "the task metadata identity no longer matches the active workspace";

            var temporary = context.MetadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(current with { BaseSha = baseSha }, JsonOptions),
                    new UTF8Encoding(false),
                    ct);
                File.Move(temporary, context.MetadataPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(temporary); } catch { }
                throw;
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return "metadata write failed (" + exception.GetType().Name + ")";
        }
    }

    private static List<string>? ReadPathList(
        GitContext context,
        JsonObject args,
        out string? error)
    {
        error = null;
        if (!args.ContainsKey("paths"))
            return null;
        if (args["paths"] is not JsonArray array || array.Count is < 1 or > 64
            || array.Any(item => item is not JsonValue value || value.GetValueKind() != JsonValueKind.String))
        {
            error = "paths must be an array of 1 to 64 project-relative strings.";
            return null;
        }

        var paths = new List<string>();
        foreach (var item in array)
        {
            if (!TryResolvePath(context, item!.GetValue<string>(), out var path, out error))
                return null;
            paths.Add(path);
        }
        return paths.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string? ReadOptionalPath(
        GitContext context,
        JsonObject args,
        string property,
        out string? error)
    {
        error = null;
        if (!args.ContainsKey(property))
            return null;
        if (args[property] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            error = property + " must be a project-relative string.";
            return null;
        }
        return TryResolvePath(context, value.GetValue<string>(), out var path, out error) ? path : null;
    }

    private static bool TryResolvePath(
        GitContext context,
        string input,
        out string path,
        out string? error)
    {
        path = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxPathLength || input.Contains('\0'))
        {
            error = "Paths must be non-empty project-relative strings of at most 400 characters.";
            return false;
        }

        var normalized = input.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Split('/').Any(segment =>
                segment is "" or "." or ".."
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            error = "Absolute paths, traversal, .git paths and pathspec syntax are not allowed.";
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(
            context.ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(context.ProjectRoot, full))
        {
            error = "The path is outside the authorized project directory.";
            return false;
        }

        var current = context.ProjectRoot;
        foreach (var segment in normalized.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
            {
                error = "Paths through symbolic links or junctions are not allowed.";
                return false;
            }
        }

        path = Path.GetRelativePath(context.WorktreeRoot, full).Replace('\\', '/');
        return IsWithin(context.WorktreeRoot, full)
            ? true
            : Fail("The path is outside the task worktree.", out error);
    }

    private static bool IsProjectRelativePath(GitContext context, string repositoryRelative)
    {
        if (string.IsNullOrWhiteSpace(repositoryRelative)
            || Path.IsPathRooted(repositoryRelative)
            || repositoryRelative.Contains((char)92)
            || repositoryRelative.Contains(':')
            || repositoryRelative.Split('/').Any(segment =>
                segment is "" or "." or ".."
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            return false;

        var current = context.WorktreeRoot;
        foreach (var segment in repositoryRelative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
                return false;
        }

        var full = Path.GetFullPath(Path.Combine(
            context.WorktreeRoot, repositoryRelative.Replace('/', Path.DirectorySeparatorChar)));
        return IsWithin(context.ProjectRoot, full);
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static void AddProjectPathspec(GitContext context, List<string> args, string? path = null)
    {
        args.Add("--");
        if (path is not null)
            args.Add(Pathspec(path));
        else
            args.Add(Pathspec(context.ProjectRelativePath.Length > 0
                ? context.ProjectRelativePath + "/"
                : "."));
    }

    private static string Pathspec(string path) => ":(literal)" + path;

    internal static Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> Git(
        string directory,
        IReadOnlyCollection<string> filters,
        CancellationToken ct,
        params string[] arguments) =>
        AgentSessionWorktree.RunGitAsync(directory, ct, filters, arguments);

    internal static bool Succeeded((int ExitCode, string StdOut, string StdErr, bool TimedOut) result) =>
        result.ExitCode == 0 && !result.TimedOut;

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static bool IsGitSha(string value) =>
        (value.Length == 40 || value.Length == 64) && value.All(Uri.IsHexDigit);

    private static string ReadString(JsonObject args, string key) =>
        args[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static int? ReadInt(JsonObject args, string key) =>
        args[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    internal static string RedactCredentialValues(string value, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(lookup);
        var safe = value;
        foreach (var variable in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN" })
        {
            var secret = lookup(variable);
            if (!string.IsNullOrEmpty(secret))
                safe = safe.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }
        return safe;
    }

    internal static string Clip(string value)
    {
        var safe = RedactCredentialValues(value, Environment.GetEnvironmentVariable);
        return safe.Length <= MaxOutputCharacters
            ? safe
            : safe[..MaxOutputCharacters] + "\n[output truncated]";
    }

    internal static McpToolResult Success<T>(T value) =>
        new(false, JsonSerializer.Serialize(value, JsonOptions));

    internal static McpToolResult Error(string message) => new(true, message);
    internal static McpToolResult GitFailure(string message, string? detail = null) =>
        new(true, string.IsNullOrWhiteSpace(detail) ? message : message + " " + Clip(detail.Trim()));
}
