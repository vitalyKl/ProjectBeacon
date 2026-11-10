namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.Security.Cryptography;
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

    private sealed record FileSnapshot(string Path, byte[]? Bytes);

    public async Task<(bool Ok, string? Result, string? Error)> ExecuteAsync(
        string projectRoot,
        Guid expectedProjectId,
        string payloadJson,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            return (false, null, "RepoPatchConflict: ProjectRuntimeMissing");

        if (!TryReadRequest(payloadJson, expectedProjectId, out var request, out var error))
            return (false, null, error);

        var root = Path.GetFullPath(projectRoot);
        var gitRootResult = await RunGitAsync(root, ["rev-parse", "--show-toplevel"], null, ct);
        if (gitRootResult.ExitCode != 0 || gitRootResult.TimedOut || string.IsNullOrWhiteSpace(gitRootResult.StdOut))
            return (false, null, "RepoPatchConflict: GitRepositoryMissing");

        var repositoryRoot = Path.GetFullPath(gitRootResult.StdOut.Trim());
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

        using var operationLock = await GitRepositoryOperationGate.AcquireAsync(repositoryRoot, ct);
        if (GitRepositoryOperationGate.HasActiveSessions(repositoryRoot))
            return (false, null, "RepoPatchConflict: ActiveRepositorySessions");

        var snapshots = new List<FileSnapshot>(request.Files.Count);
        foreach (var file in request.Files)
        {
            var resolved = ResolveSafeFile(root, file.ProjectPath);
            if (resolved is null)
                return (false, null, "RepoPatchConflict: UnsafeDocumentationPath");

            byte[]? bytes = null;
            if (File.Exists(resolved))
            {
                if ((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0
                    || Directory.Exists(resolved))
                    return (false, null, "RepoPatchConflict: SymbolicLinkOrDirectory");
                bytes = await File.ReadAllBytesAsync(resolved, ct);
            }
            else if (Directory.Exists(resolved))
            {
                return (false, null, "RepoPatchConflict: TargetIsDirectory");
            }

            if (file.BaseHash is null)
            {
                if (bytes is not null)
                    return (false, null, "RepoPatchConflict: TargetAlreadyExists");
            }
            else if (bytes is null || !FixedEquals(Hash(bytes), file.BaseHash))
            {
                return (false, null, "RepoPatchConflict: BaseHashMismatch");
            }

            snapshots.Add(new FileSnapshot(resolved, bytes));
        }

        var check = await RunGitAsync(repositoryRoot, ["apply", "--check", "--whitespace=nowarn"], request.Patch, ct);
        if (check.TimedOut)
            return (false, null, "RepoPatchConflict: GitApplyCheckTimeout");
        if (check.ExitCode != 0)
            return (false, null, "RepoPatchConflict: GitApplyCheckFailed");

        var applied = await RunGitAsync(repositoryRoot, ["apply", "--whitespace=nowarn"], request.Patch, ct);
        if (applied.TimedOut || applied.ExitCode != 0)
        {
            await RestoreAsync(snapshots, CancellationToken.None);
            return (false, null, applied.TimedOut
                ? "RepoPatchConflict: GitApplyTimeout"
                : "RepoPatchConflict: GitApplyFailed");
        }

        var results = new List<object>(request.Files.Count);
        foreach (var file in request.Files)
        {
            var resolved = ResolveSafeFile(root, file.ProjectPath);
            if (resolved is null)
            {
                await RestoreAsync(snapshots, CancellationToken.None);
                return (false, null, "RepoPatchConflict: PostApplyPathInvalid");
            }

            if (file.ResultHash is null)
            {
                if (File.Exists(resolved) || Directory.Exists(resolved))
                {
                    await RestoreAsync(snapshots, CancellationToken.None);
                    return (false, null, "RepoPatchConflict: DeleteVerificationFailed");
                }
                results.Add(new { projectPath = file.ProjectPath, deleted = true });
                continue;
            }

            if (!File.Exists(resolved)
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
            {
                await RestoreAsync(snapshots, CancellationToken.None);
                return (false, null, "RepoPatchConflict: ResultFileMissing");
            }

            var resultHash = Hash(await File.ReadAllBytesAsync(resolved, ct));
            if (!FixedEquals(resultHash, file.ResultHash))
            {
                await RestoreAsync(snapshots, CancellationToken.None);
                return (false, null, "RepoPatchConflict: ResultHashMismatch");
            }
            results.Add(new { projectPath = file.ProjectPath, resultHash });
        }

        var resultJson = JsonSerializer.Serialize(new
        {
            projectId = request.ProjectId,
            proposalId = request.ProposalId,
            applied = true,
            files = results
        });
        return (true, resultJson, null);
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

    private static string? ResolveSafeFile(string root, string relativePath)
    {
        if (!BuiltInAgentProfiles.IsSafeRelativePath(relativePath)
            || !BuiltInAgentProfiles.IsWithinDefaults(relativePath, DocumentationDefaults))
            return null;

        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(
            fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            return null;

        var current = fullRoot;
        var segments = relativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            if (index < segments.Length - 1 && !Directory.Exists(current))
                return null;
        }
        return candidate;
    }

    private static async Task RestoreAsync(IReadOnlyList<FileSnapshot> snapshots, CancellationToken ct)
    {
        foreach (var snapshot in snapshots)
        {
            try
            {
                if (snapshot.Bytes is null)
                {
                    if (File.Exists(snapshot.Path))
                        File.Delete(snapshot.Path);
                    continue;
                }

                var directory = Path.GetDirectoryName(snapshot.Path)!;
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".restore");
                await File.WriteAllBytesAsync(temporary, snapshot.Bytes, ct);
                File.Move(temporary, snapshot.Path, overwrite: true);
            }
            catch
            {
                // A later manual review will see the failed command; never hide the original error.
            }
        }
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
        start.ArgumentList.Add("--no-pager");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.hooksPath=" + Path.Combine(Path.GetTempPath(), "projectbeacon-empty-hooks"));
        start.ArgumentList.Add("apply");
        // The caller supplies subcommands as argument[0]; discard the sentinel above for rev-parse.
        start.ArgumentList.Clear();
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
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
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();
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
