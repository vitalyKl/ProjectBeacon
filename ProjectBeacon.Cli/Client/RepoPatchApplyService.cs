namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectBeacon.Application.Proposals;
using ProjectBeacon.Application.Tasks;

/// <summary>
/// Applies one approved documentation-only RepoPatch on the workstation.
/// Every path and hash is revalidated locally; failed verification restores the pre-apply bytes.
/// </summary>
internal sealed class RepoPatchApplyService
{
    private const int MaxOutputCharacters = 16_000;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(45);
    private static readonly string[] DocumentationDefaults = ["docs", "README.md"];

    private sealed record PatchRequest(
        Guid ProjectId,
        Guid ProposalId,
        string Patch,
        IReadOnlyList<RepoPatchFile> Files,
        IReadOnlyList<string> AllowedPaths);

    private sealed record FileSnapshot(string RelativePath, string Path, byte[]? Bytes);

    public async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(
        string projectRoot,
        Guid expectedProjectId,
        string payloadJson,
        CancellationToken ct,
        Func<string, CancellationToken, Task>? reportProgress = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            return (false, null, "RepoPatchConflict: ProjectRuntimeMissing");

        if (!TryReadRequest(payloadJson, expectedProjectId, out var request, out var error))
            return (false, null, error);

        if (!TryResolvePhysicalDirectory(projectRoot, out var root))
            return (false, null, "RepoPatchConflict: ProjectRuntimeMissing");

        var gitRootResult = await RunGitAsync(root, ["rev-parse", "--show-toplevel"], null, ct);
        if (gitRootResult.ExitCode != 0 || gitRootResult.TimedOut || string.IsNullOrWhiteSpace(gitRootResult.StdOut))
            return (false, null, "RepoPatchConflict: GitRepositoryMissing");

        if (!TryResolvePhysicalDirectory(gitRootResult.StdOut.Trim(), out var repositoryRoot))
            return (false, null, "RepoPatchConflict: GitRepositoryMissing");
        var rootPrefix = Path.GetRelativePath(repositoryRoot, root);
        if (rootPrefix == ".." || rootPrefix.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(rootPrefix))
            return (false, null, "RepoPatchConflict: ProjectRootOutsideRepository");
        var gitPrefix = rootPrefix == "."
            ? string.Empty
            : rootPrefix.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').TrimEnd('/') + "/";

        foreach (var file in request!.Files)
        {
            var expectedGitPath = gitPrefix + file.ProjectPath;
            if (!string.Equals(file.GitPath, expectedGitPath, StringComparison.Ordinal))
                return (false, null, "RepoPatchConflict: GitPathMappingMismatch");
        }

        IDisposable operationLock;
        while (true)
        {
            var idle = GitRepositoryOperationGate.WaitForNoSessionsAsync(repositoryRoot, ct);
            if (!idle.IsCompleted && reportProgress is not null)
                await reportProgress(
                    "Waiting for the current agent session to finish before applying the approved documentation patch.",
                    ct);

            if (!idle.IsCompleted)
            {
                var progressInterval = Task.Delay(TimeSpan.FromSeconds(10), ct);
                await Task.WhenAny(idle, progressInterval);
                ct.ThrowIfCancellationRequested();
                if (!idle.IsCompleted)
                    continue;
            }
            await idle;

            operationLock = await GitRepositoryOperationGate.AcquireAsync(repositoryRoot, ct);
            if (!GitRepositoryOperationGate.HasActiveSessions(repositoryRoot))
                break;

            // A new session can register between the idle signal and acquiring the operation
            // gate. Release and wait again; never hold the gate while waiting for session leases,
            // because a worktree needs the same gate to dispose and release its lease.
            operationLock.Dispose();
        }

        using (operationLock)
        {
            var snapshots = new List<FileSnapshot>(request.Files.Count);
            var filesToApply = new List<RepoPatchFile>(request.Files.Count);
            foreach (var file in request.Files)
            {
                var resolved = ResolveSafeFile(root, file.ProjectPath);
                if (resolved is null)
                    return (false, null, "RepoPatchConflict: UnsafeDocumentationPath");

                byte[]? bytes = null;
                if (IsLinkOrReparsePoint(resolved))
                    return (false, null, "RepoPatchConflict: SymbolicLinkOrDirectory");
                if (File.Exists(resolved))
                {
                    bytes = await File.ReadAllBytesAsync(resolved, ct);
                }
                else if (Directory.Exists(resolved))
                {
                    return (false, null, "RepoPatchConflict: TargetIsDirectory");
                }

                // Preserve the exact pre-attempt state for rollback. A retry can arrive after
                // the previous attempt applied some or all files but lost its completion ACK.
                snapshots.Add(new FileSnapshot(file.ProjectPath, resolved, bytes));

                if (MatchesResultState(file, bytes))
                    continue;

                if (!MatchesBaseState(file, bytes))
                {
                    var reason = file.BaseHash is null && bytes is not null
                        ? "TargetAlreadyExists"
                        : "BaseHashMismatch";
                    return (false, null, $"RepoPatchConflict: {reason}");
                }

                filesToApply.Add(file);
            }

            if (filesToApply.Count > 0)
            {
                var patchToApply = FilterPatch(request.Patch, filesToApply);
                if (string.IsNullOrWhiteSpace(patchToApply))
                    return (false, null, "RepoPatchConflict: InvalidPatchManifest");

                var check = await RunGitAsync(repositoryRoot, ["apply", "--check", "--whitespace=nowarn"], patchToApply, ct);
                if (check.TimedOut)
                    return (false, null, "RepoPatchConflict: GitApplyCheckTimeout");
                if (check.ExitCode != 0)
                    return (false, null, "RepoPatchConflict: GitApplyCheckFailed");

                var applied = await RunGitAsync(repositoryRoot, ["apply", "--whitespace=nowarn"], patchToApply, ct);
                if (applied.TimedOut || applied.ExitCode != 0)
                {
                    var restored = await RestoreAsync(root, snapshots, CancellationToken.None);
                    var reason = applied.TimedOut ? "GitApplyTimeout" : "GitApplyFailed";
                    return (false, null, restored
                        ? $"RepoPatchConflict: {reason}"
                        : $"RepoPatchConflict: {reason}:RollbackFailed");
                }
            }

            var results = new List<object>(request.Files.Count);
            foreach (var file in request.Files)
            {
                var resolved = ResolveSafeFile(root, file.ProjectPath);
                if (resolved is null)
                {
                    var restored = await RestoreAsync(root, snapshots, CancellationToken.None);
                    return (false, null, restored
                        ? "RepoPatchConflict: PostApplyPathInvalid"
                        : "RepoPatchConflict: PostApplyPathInvalid:RollbackFailed");
                }

                if (file.ResultHash is null)
                {
                    if (File.Exists(resolved) || Directory.Exists(resolved))
                    {
                        var restored = await RestoreAsync(root, snapshots, CancellationToken.None);
                        return (false, null, restored
                            ? "RepoPatchConflict: DeleteVerificationFailed"
                            : "RepoPatchConflict: DeleteVerificationFailed:RollbackFailed");
                    }
                    results.Add(new { projectPath = file.ProjectPath, deleted = true });
                    continue;
                }

                if (!File.Exists(resolved) || IsLinkOrReparsePoint(resolved))
                {
                    var restored = await RestoreAsync(root, snapshots, CancellationToken.None);
                    return (false, null, restored
                        ? "RepoPatchConflict: ResultFileMissing"
                        : "RepoPatchConflict: ResultFileMissing:RollbackFailed");
                }

                var resultHash = Hash(await File.ReadAllBytesAsync(resolved, ct));
                if (!FixedEquals(resultHash, file.ResultHash))
                {
                    var restored = await RestoreAsync(root, snapshots, CancellationToken.None);
                    return (false, null, restored
                        ? "RepoPatchConflict: ResultHashMismatch"
                        : "RepoPatchConflict: ResultHashMismatch:RollbackFailed");
                }
                results.Add(new { projectPath = file.ProjectPath, resultHash });
            }

            var resultJson = JsonSerializer.Serialize(new
            {
                projectId = request.ProjectId,
                proposalId = request.ProposalId,
                applied = true,
                alreadyApplied = filesToApply.Count == 0,
                files = results
            });
            return (true, resultJson, null);
        }
    }

    private static bool TryReadRequest(
        string payloadJson,
        Guid expectedProjectId,
        out PatchRequest? request,
        out string? error)
    {
        request = null;
        error = "RepoPatchConflict: InvalidPayload";
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 5
                || !root.TryGetProperty("projectId", out var rawProject)
                || rawProject.ValueKind != JsonValueKind.String
                || !Guid.TryParse(rawProject.GetString(), out var projectId)
                || projectId != expectedProjectId
                || !root.TryGetProperty("proposalId", out var rawProposal)
                || rawProposal.ValueKind != JsonValueKind.String
                || !Guid.TryParse(rawProposal.GetString(), out var proposalId)
                || proposalId == Guid.Empty
                || !root.TryGetProperty("allowedPaths", out var rawAllowed)
                || rawAllowed.ValueKind != JsonValueKind.Array
                || rawAllowed.GetArrayLength() is < 1 or > 10)
                return false;

            var allowed = rawAllowed.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
            if (allowed.Length != rawAllowed.GetArrayLength()
                || allowed.Any(path => !BuiltInAgentProfiles.IsWithinDefaults(path, DocumentationDefaults)))
                return false;

            if (!root.TryGetProperty("patch", out var rawPatch)
                || rawPatch.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("files", out var rawFiles)
                || rawFiles.ValueKind != JsonValueKind.Array)
                return false;

            var manifest = JsonSerializer.Serialize(new
            {
                patch = rawPatch.GetString(),
                files = rawFiles
            });
            if (!RepoPatchValidation.TryValidate(manifest, allowed, DocumentationDefaults, out var validated, out _)
                || validated is null)
                return false;

            request = new PatchRequest(projectId, proposalId, validated.Patch, validated.Files, allowed);
            error = null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool MatchesBaseState(RepoPatchFile file, byte[]? bytes) =>
        file.BaseHash is null
            ? bytes is null
            : bytes is not null && FixedEquals(Hash(bytes), file.BaseHash);

    private static bool MatchesResultState(RepoPatchFile file, byte[]? bytes) =>
        file.ResultHash is null
            ? bytes is null
            : bytes is not null && FixedEquals(Hash(bytes), file.ResultHash);

    /// <summary>
    /// Removes diff sections that have already reached their manifest result hash. This makes
    /// recovery safe after a process restart between file application and command completion.
    /// </summary>
    private static string FilterPatch(string patch, IReadOnlyCollection<RepoPatchFile> files)
    {
        var allowed = files.Select(file => file.GitPath).ToHashSet(StringComparer.Ordinal);
        var builder = new StringBuilder(patch.Length);
        var includeSection = false;
        var sawSection = false;

        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git a/", StringComparison.Ordinal))
            {
                var separator = line.IndexOf(" b/", "diff --git a/".Length, StringComparison.Ordinal);
                if (separator < 0)
                    return string.Empty;
                var gitPath = line["diff --git a/".Length..separator];
                includeSection = allowed.Contains(gitPath);
                sawSection = true;
            }

            if (sawSection && includeSection)
                builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Resolves every directory segment, not only the leaf. On macOS, for example,
    /// the temporary directory may be reached through /var while Git reports its
    /// physical location under /private/var. Comparing lexical paths would reject
    /// a valid repository as an escape.
    /// </summary>
    private static bool TryResolvePhysicalDirectory(string path, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var current = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(current))
                return false;

            var remainder = fullPath[current.Length..];
            var segments = remainder.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                var candidate = Path.Combine(current, segment);
                var directory = new DirectoryInfo(candidate);
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                current = Path.GetFullPath(target?.FullName ?? candidate);
            }

            if (!Directory.Exists(current))
                return false;

            resolved = Path.TrimEndingDirectorySeparator(current);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? ResolveSafeFile(string root, string relativePath)
    {
        if (!BuiltInAgentProfiles.IsSafeRelativePath(relativePath)
            || !BuiltInAgentProfiles.IsWithinDefaults(relativePath, DocumentationDefaults))
            return null;

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(
            fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(fullRoot, candidate);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            return null;

        var current = fullRoot;
        var segments = relativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (IsLinkOrReparsePoint(current))
                return null;
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if (index < segments.Length - 1 && !Directory.Exists(current))
                return null;
        }
        return candidate;
    }

    private static bool IsLinkOrReparsePoint(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null)
                return true;
            if (File.Exists(path) || Directory.Exists(path))
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            // Unreadable path metadata is not safe to follow.
            return true;
        }
    }

    private static async Task<bool> RestoreAsync(
        string root,
        IReadOnlyList<FileSnapshot> snapshots,
        CancellationToken ct)
    {
        var restored = true;
        foreach (var snapshot in snapshots)
        {
            string? temporary = null;
            try
            {
                var safe = ResolveSafeFile(root, snapshot.RelativePath);
                if (safe is null || !string.Equals(safe, snapshot.Path,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    restored = false;
                    continue;
                }

                if (snapshot.Bytes is null)
                {
                    if (File.Exists(safe))
                    {
                        if (IsLinkOrReparsePoint(safe))
                        {
                            restored = false;
                            continue;
                        }
                        File.Delete(safe);
                    }
                    continue;
                }

                var directory = Path.GetDirectoryName(safe)!;
                Directory.CreateDirectory(directory);
                if (IsLinkOrReparsePoint(directory))
                {
                    restored = false;
                    continue;
                }
                temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".restore");
                await File.WriteAllBytesAsync(temporary, snapshot.Bytes, ct);
                File.Move(temporary, safe, overwrite: true);
                temporary = null;
            }
            catch
            {
                restored = false;
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); } catch { }
                }
            }
        }
        return restored;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record GitResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

    private static async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken ct)
    {
        using var process = new Process();
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment.Remove("GIT_CONFIG_COUNT");
        start.Environment.Remove("GIT_CONFIG_PARAMETERS");
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["PAGER"] = "cat";
        start.Environment.Remove("GIT_DIR");
        start.Environment.Remove("GIT_WORK_TREE");
        start.Environment.Remove("GIT_INDEX_FILE");
        start.Environment.Remove("GIT_CONFIG_PARAMETERS");
        process.StartInfo = start;

        try
        {
            if (!process.Start())
                return new GitResult(-1, "", "Git did not start.", false);
        }
        catch
        {
            return new GitResult(-1, "", "Git could not start.", false);
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput);
        var stderrTask = ReadBoundedAsync(process.StandardError);
        if (standardInput is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
                process.StandardInput.Close();
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.StandardInput.Close(); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
                throw;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new GitResult(timedOut ? -1 : process.ExitCode,
            stdout, stderr, timedOut);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new System.Text.StringBuilder();
        var buffer = new char[2048];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0)
                break;
            if (builder.Length < MaxOutputCharacters)
                builder.Append(buffer, 0, Math.Min(count, MaxOutputCharacters - builder.Length));
        }
        return builder.ToString();
    }
}
