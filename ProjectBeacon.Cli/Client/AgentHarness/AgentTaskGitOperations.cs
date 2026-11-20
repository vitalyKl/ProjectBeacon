namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
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

    private sealed record GitContext(
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

    private sealed record OperationState(bool Rebase, bool Merge)
    {
        public bool Active => Rebase || Merge;
    }

    public static bool IsGitTool(string? name) =>
        name is PipelineToolPolicy.GitInfo
            or PipelineToolPolicy.GitCommit
            or PipelineToolPolicy.GitIntegrate
            or PipelineToolPolicy.GitPublish;

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
            "Refresh this task branch from its daemon-selected base branch, or continue/abort a reported conflict. Remote and branch are never supplied by the agent.",
            new JsonObject
            {
                ["action"] = EnumProperty("integrate", "continue", "abort"),
                ["strategy"] = EnumProperty("rebase", "merge")
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
                return Success(new
                {
                    branch = context.BranchName,
                    baseBranch = context.BaseBranch,
                    baseSha = context.BaseSha
                });

            case "status":
                {
                    var statusArgs = new List<string> { "status", "--short", "--branch", "--untracked-files=all" };
                    AddProjectPathspec(context, statusArgs);
                    var status = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. statusArgs]);
                    var head = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--verify", "HEAD");
                    if (!Succeeded(status) || !Succeeded(head))
                        return GitFailure("Could not read task branch status.");
                    return Success(new
                    {
                        branch = context.BranchName,
                        baseBranch = context.BaseBranch,
                        baseSha = context.BaseSha,
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
                    var diffArgs = new List<string> { "diff", "--no-ext-diff", "--no-color", "--find-renames", context.BaseSha };
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
        var action = ReadString(args, "action");
        if (action is not ("integrate" or "continue" or "abort"))
            return Error("git_integrate action must be integrate, continue, or abort.");

        if (action == "integrate")
        {
            var strategy = ReadString(args, "strategy");
            if (strategy is not ("rebase" or "merge"))
                return Error("git_integrate action=integrate requires strategy=rebase or strategy=merge.");

            var state = await ReadOperationStateAsync(context, ct);
            if (state.Active)
                return Error("A merge or rebase is already in progress. Use action=continue or action=abort.");

            var status = await Git(context.WorktreeRoot, context.DisabledFilters, ct,
                "status", "--porcelain", "--untracked-files=all");
            if (!Succeeded(status))
                return GitFailure("Could not inspect task branch status before integration.");
            if (!string.IsNullOrWhiteSpace(status.StdOut))
                return Error("Commit or discard task worktree changes with git_commit before integrating the base branch.");

            var target = await FetchBaseAsync(context, ct);
            if (target.Error is not null)
                return Error(target.Error);

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
                var mergeConflicts = operation.Active ? await ReadConflictsAsync(context, ct) : [];
                if (mergeConflicts.Count > 0)
                    return Success(new { status = "conflict", strategy, targetSha = target.Sha, files = mergeConflicts });
                return GitFailure("Git could not integrate the task base branch. No resolvable conflict state was opened.");
            }

            var update = await UpdateMetadataBaseShaAsync(context, target.Sha!, ct);
            if (update is not null)
                return Error("The branch was integrated, but the saved base checkpoint could not be updated: " + update);
            return Success(new { status = "integrated", strategy, baseBranch = context.BaseBranch, baseSha = target.Sha });
        }

        var active = await ReadOperationStateAsync(context, ct);
        if (!active.Active)
            return Error("There is no merge or rebase in progress for this task branch.");

        if (action == "abort")
        {
            var abort = active.Rebase
                ? await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rebase", "--abort")
                : await Git(context.WorktreeRoot, context.DisabledFilters, ct, "merge", "--abort");
            return Succeeded(abort)
                ? Success(new { status = "aborted" })
                : GitFailure("Could not abort the in-progress Git integration.");
        }

        var conflicts = await ReadConflictsAsync(context, ct);
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

        var remaining = await ReadConflictsAsync(context, ct);
        if (remaining.Count > 0)
            return Success(new { status = "conflict", files = remaining });

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
            var nextConflicts = stillActive.Active ? await ReadConflictsAsync(context, ct) : [];
            if (nextConflicts.Count > 0)
                return Success(new { status = "conflict", files = nextConflicts });
            return GitFailure("Git could not continue the integration.");
        }

        var baseRef = IntegrationBaseRef(context.TaskId);
        var targetHead = await Git(context.RepositoryRoot, context.DisabledFilters, ct, "rev-parse", "--verify", baseRef);
        if (!Succeeded(targetHead) || !IsGitSha(targetHead.StdOut.Trim()))
            return GitFailure("The integration finished but its fetched base commit could not be verified.");

        var metadataError = await UpdateMetadataBaseShaAsync(context, targetHead.StdOut.Trim(), ct);
        if (metadataError is not null)
            return Error("The integration finished, but the saved base checkpoint could not be updated: " + metadataError);
        return Success(new { status = "integrated", baseBranch = context.BaseBranch, baseSha = targetHead.StdOut.Trim() });
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

            var fetch = await Git(context.RepositoryRoot, context.DisabledFilters, ct,
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

            // Keep one task-specific integration target so continue can verify and persist
            // the exact base SHA even when this repository has no origin remote.
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

    private static string IntegrationBaseRef(Guid taskId) => $"refs/beacon/task/{taskId:N}/integration-base";

    private sealed record PullRequestInfo(int Number, string Url, string BaseRefName, string State, bool IsDraft);

    private static async Task<McpToolResult> PublishAsync(
        GitContext context,
        JsonObject args,
        CancellationToken ct)
    {
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
        if (operation.Active)
            return Error("Resolve the active merge or rebase with git_integrate before publishing.");

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
            "diff", "--name-only", "-z", context.BaseSha + ".." + headSha);
        if (!Succeeded(changedPaths))
            return GitFailure("Could not verify the task branch change scope before publishing.", changedPaths.StdErr);
        var changedPathList = changedPaths.StdOut
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (changedPathList.Length == 0)
            return Error("The task branch has no file changes to publish.");
        var changeSummary = await Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "diff", "--summary", context.BaseSha + ".." + headSha);
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

        var ahead = await Git(
            context.WorktreeRoot, context.DisabledFilters, ct,
            "rev-list", "--count", context.BaseSha + ".." + headSha);
        if (!Succeeded(ahead) || !int.TryParse(ahead.StdOut.Trim(), out var aheadCount) || aheadCount <= 0)
            return Error("There are no task commits to publish relative to the pinned review base.");

        var origin = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "remote", "get-url", "origin");
        if (!Succeeded(origin) || !TryParseGitHubRemote(origin.StdOut.Trim(), out var repository))
            return Error("Publishing requires origin to be a credential-free HTTPS or SSH URL for github.com/owner/repository.");

        var ghPath = WorkstationProbe.Which("gh");
        if (ghPath is null)
            return Error("GitHub publishing requires GitHub CLI (gh). Install it and run gh auth login on this workstation.");
        var auth = await RunGitHubCliAsync(ghPath, context.WorktreeRoot, ct, "auth", "status", "--hostname", "github.com");
        if (auth.ExitCode != 0 || auth.TimedOut)
            return Error("GitHub CLI is not authenticated for github.com. Run gh auth login on this workstation; no credential was requested or stored by ProjectBeacon.");

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
            return Success(new
            {
                published = true,
                alreadyMerged = true,
                repository,
                branch = context.BranchName,
                baseBranch,
                headSha,
                pullRequestNumber = merged.Number,
                pullRequestUrl = merged.Url,
                draft = false
            });
        if (matchingBase is { State: "CLOSED" } closed)
            return Error(
                $"Pull request #{closed.Number} for this task branch is already closed ({closed.Url}). "
                + "Reopen it explicitly or publish a new task branch; ProjectBeacon will not silently recreate it.");

        var credentialedGit = new List<string>
        {
            "-c", "credential.helper=!gh auth git-credential"
        };
        credentialedGit.AddRange([
            "ls-remote", "--heads", "origin", "refs/heads/" + baseBranch
        ]);
        var baseOnRemote = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. credentialedGit]);
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

        // This is the only refspec: no force option, tags, extra refs, or caller-supplied remote.
        var pushArguments = new List<string>
        {
            "-c", "credential.helper=!gh auth git-credential",
            "push", "--set-upstream", "--porcelain", "origin",
            $"refs/heads/{context.BranchName}:refs/heads/{context.BranchName}"
        };
        var push = await Git(context.WorktreeRoot, context.DisabledFilters, ct, [.. pushArguments]);
        if (!Succeeded(push))
            return GitFailure(
                "Could not publish the task branch. The push is non-forced; resolve any remote divergence and retry.",
                push.StdErr);

        if (matchingBase is { State: "OPEN" } open)
            return Success(new
            {
                published = true,
                pullRequestCreated = false,
                repository,
                branch = context.BranchName,
                baseBranch,
                headSha,
                pullRequestNumber = open.Number,
                pullRequestUrl = open.Url,
                draft = open.IsDraft
            });

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
                return Success(new
                {
                    published = true,
                    pullRequestCreated = false,
                    repository,
                    branch = context.BranchName,
                    baseBranch,
                    headSha,
                    pullRequestNumber = raced.Number,
                    pullRequestUrl = raced.Url,
                    draft = raced.IsDraft
                });
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

        return Success(new
        {
            published = true,
            pullRequestCreated = true,
            repository,
            branch = context.BranchName,
            baseBranch,
            headSha,
            pullRequestUrl,
            draft
        });
    }

    private static async Task<(IReadOnlyList<PullRequestInfo> PullRequests, string? Error)> ListPullRequestsAsync(
        string ghPath,
        string workingDirectory,
        string repository,
        string branch,
        CancellationToken ct)
    {
        var owner = repository.Split('/')[0];
        var result = await RunGitHubCliAsync(
            ghPath, workingDirectory, ct,
            "pr", "list", "--repo", repository,
            "--head", owner + ":" + branch,
            "--state", "all",
            "--json", "number,url,baseRefName,state,isDraft");
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

    private static async Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> RunGitHubCliAsync(
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
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return await ProcessRunner.RunAsync(info, TimeSpan.FromSeconds(90), ct);
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

    private static async Task<OperationState> ReadOperationStateAsync(GitContext context, CancellationToken ct)
    {
        var rebaseMerge = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--git-path", "rebase-merge");
        var rebaseApply = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--git-path", "rebase-apply");
        var mergeHead = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "rev-parse", "--git-path", "MERGE_HEAD");

        bool Exists((int ExitCode, string StdOut, string StdErr, bool TimedOut) result)
        {
            if (!Succeeded(result) || string.IsNullOrWhiteSpace(result.StdOut))
                return false;
            var path = result.StdOut.Trim();
            if (!Path.IsPathRooted(path))
                path = Path.GetFullPath(Path.Combine(context.WorktreeRoot, path));
            return Directory.Exists(path) || File.Exists(path);
        }

        return new OperationState(
            Exists(rebaseMerge) || Exists(rebaseApply),
            Exists(mergeHead));
    }

    private static async Task<List<string>> ReadConflictsAsync(GitContext context, CancellationToken ct)
    {
        var result = await Git(context.WorktreeRoot, context.DisabledFilters, ct, "ls-files", "-u", "-z");
        if (!Succeeded(result))
            return [];
        return result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry =>
            {
                var tab = entry.IndexOf('\t');
                return tab >= 0 ? entry[(tab + 1)..] : string.Empty;
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
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

    private static Task<(int ExitCode, string StdOut, string StdErr, bool TimedOut)> Git(
        string directory,
        IReadOnlyCollection<string> filters,
        CancellationToken ct,
        params string[] arguments) =>
        AgentSessionWorktree.RunGitAsync(directory, ct, filters, arguments);

    private static bool Succeeded((int ExitCode, string StdOut, string StdErr, bool TimedOut) result) =>
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

    private static bool IsGitSha(string value) =>
        (value.Length == 40 || value.Length == 64) && value.All(Uri.IsHexDigit);

    private static string ReadString(JsonObject args, string key) =>
        args[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static int? ReadInt(JsonObject args, string key) =>
        args[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string Clip(string value) =>
        value.Length <= MaxOutputCharacters ? value : value[..MaxOutputCharacters] + "\n[output truncated]";

    private static McpToolResult Success<T>(T value) =>
        new(false, JsonSerializer.Serialize(value, JsonOptions));

    private static McpToolResult Error(string message) => new(true, message);
    private static McpToolResult GitFailure(string message, string? detail = null) =>
        new(true, string.IsNullOrWhiteSpace(detail) ? message : message + " " + Clip(detail.Trim()));
}
