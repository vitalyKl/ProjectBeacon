namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectBeacon.Application.Proposals;
using ProjectBeacon.Cli.Client;

public sealed class RepoPatchApplyServiceTests
{
    private readonly RepoPatchApplyService _service = new();

    [Fact]
    public async Task Execute_AppliesApprovedDocumentationPatch_AndReturnsVerifiedHashes()
    {
        await using var repo = await RepositoryFixture.CreateAsync(nestedProject: false);
        const string path = "docs/guide.md";
        const string before = "# Guide\nOld instructions.\n";
        const string after = "# Guide\nApproved instructions.\n";
        await repo.WriteCommittedAsync(path, before);
        var patch = await repo.CreatePatchAsync(path, after);
        var payload = Payload(repo.ProjectId, patch, path, before, after, proposalId: repo.ProposalId);

        var result = await _service.ExecuteAsync(repo.ProjectRoot, repo.ProjectId, payload, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(after, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path)));
        using var response = JsonDocument.Parse(result.Result!);
        Assert.Equal(repo.ProjectId, response.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal(repo.ProposalId, response.RootElement.GetProperty("proposalId").GetGuid());
        Assert.True(response.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(Hash(after), response.RootElement.GetProperty("files")[0].GetProperty("resultHash").GetString());
    }

    [Fact]
    public async Task Execute_RetryAfterSuccessfulApply_IsIdempotentlyConfirmed()
    {
        await using var repo = await RepositoryFixture.CreateAsync(nestedProject: false);
        const string path = "docs/guide.md";
        const string before = "# Guide\nOriginal instructions.\n";
        const string after = "# Guide\nApproved instructions.\n";
        await repo.WriteCommittedAsync(path, before);
        var patch = await repo.CreatePatchAsync(path, after);
        var payload = Payload(repo.ProjectId, patch, path, before, after, proposalId: repo.ProposalId);

        var first = await _service.ExecuteAsync(repo.ProjectRoot, repo.ProjectId, payload, CancellationToken.None);
        Assert.True(first.Ok, first.Error);
        Assert.Equal(after, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path)));

        // Simulate a lost device completion ACK: the same idempotent command is delivered again.
        var retry = await _service.ExecuteAsync(repo.ProjectRoot, repo.ProjectId, payload, CancellationToken.None);

        Assert.True(retry.Ok, retry.Error);
        Assert.Equal(after, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path)));
        using var response = JsonDocument.Parse(retry.Result!);
        Assert.Equal(repo.ProjectId, response.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal(repo.ProposalId, response.RootElement.GetProperty("proposalId").GetGuid());
        Assert.True(response.RootElement.GetProperty("applied").GetBoolean());
        Assert.True(response.RootElement.GetProperty("alreadyApplied").GetBoolean());
        Assert.Equal(Hash(after), response.RootElement.GetProperty("files")[0].GetProperty("resultHash").GetString());
    }

    [Fact]
    public async Task Execute_RefusesChangedBaseHash_WithoutChangingWorkingTree()
    {
        await using var repo = await RepositoryFixture.CreateAsync(nestedProject: false);
        const string path = "docs/guide.md";
        const string before = "# Guide\nOriginal.\n";
        const string after = "# Guide\nApproved.\n";
        await repo.WriteCommittedAsync(path, before);
        var patch = await repo.CreatePatchAsync(path, after);
        var payload = Payload(repo.ProjectId, patch, path, before, after);

        await File.WriteAllTextAsync(Path.Combine(repo.ProjectRoot, path), "# Guide\nConcurrent edit.\n");
        var concurrent = await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path));

        var result = await _service.ExecuteAsync(repo.ProjectRoot, repo.ProjectId, payload, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("BaseHashMismatch", result.Error);
        Assert.Equal(concurrent, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path)));
    }

    [Fact]
    public async Task Execute_RestoresOriginalBytes_WhenResultHashDoesNotMatchManifest()
    {
        await using var repo = await RepositoryFixture.CreateAsync(nestedProject: false);
        const string path = "docs/guide.md";
        const string before = "# Guide\nOriginal.\n";
        const string after = "# Guide\nActual patch result.\n";
        const string wrongExpected = "# Guide\nDifferent expected result.\n";
        await repo.WriteCommittedAsync(path, before);
        var patch = await repo.CreatePatchAsync(path, after);
        var payload = Payload(repo.ProjectId, patch, path, before, wrongExpected);

        var result = await _service.ExecuteAsync(repo.ProjectRoot, repo.ProjectId, payload, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("ResultHashMismatch", result.Error);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path)));
    }

    [Fact]
    public async Task Execute_ResolvesGitPathAgainstNestedProjectRuntime()
    {
        await using var repo = await RepositoryFixture.CreateAsync(nestedProject: true);
        const string path = "docs/guide.md";
        const string before = "Before.\n";
        const string after = "After.\n";
        await repo.WriteCommittedAsync(path, before);
        var patch = await repo.CreatePatchAsync(path, after);
        var payload = Payload(repo.ProjectId, patch, path, before, after, repo.GitPrefix + path);

        var result = await _service.ExecuteAsync(repo.ProjectRoot, repo.ProjectId, payload, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(after, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path)));
    }

    [Fact]
    public async Task Execute_RetryWithPartiallyAppliedFiles_AppliesOnlyRemainingSections()
    {
        await using var repo = await RepositoryFixture.CreateAsync(nestedProject: false);
        const string firstPath = "docs/a-guide.md";
        const string secondPath = "docs/b-guide.md";
        const string firstBefore = "First base.\n";
        const string firstAfter = "First approved.\n";
        const string secondBefore = "Second base.\n";
        const string secondAfter = "Second approved.\n";
        await repo.WriteCommittedAsync(firstPath, firstBefore);
        await repo.WriteCommittedAsync(secondPath, secondBefore);

        var firstPatch = await repo.CreatePatchAsync(firstPath, firstAfter);
        var secondPatch = await repo.CreatePatchAsync(secondPath, secondAfter);
        var payload = JsonSerializer.Serialize(new
        {
            projectId = repo.ProjectId,
            proposalId = repo.ProposalId,
            // Simulate a workstation/editor that supplied Windows line endings in the
            // unified diff. Partial retry must still filter sections by exact Git path.
            patch = (firstPatch + secondPatch).Replace("\n", "\r\n", StringComparison.Ordinal),
            files = new[]
            {
                new
                {
                    gitPath = firstPath,
                    projectPath = firstPath,
                    baseHash = Hash(firstBefore),
                    resultHash = Hash(firstAfter)
                },
                new
                {
                    gitPath = secondPath,
                    projectPath = secondPath,
                    baseHash = Hash(secondBefore),
                    resultHash = Hash(secondAfter)
                }
            },
            allowedPaths = new[] { "docs" }
        });

        // Simulate a restart after the first file was written but before completion was acknowledged.
        await File.WriteAllTextAsync(Path.Combine(repo.ProjectRoot, firstPath), firstAfter);

        var result = await _service.ExecuteAsync(repo.ProjectRoot, repo.ProjectId, payload, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(firstAfter, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, firstPath)));
        Assert.Equal(secondAfter, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, secondPath)));
        using var response = JsonDocument.Parse(result.Result!);
        Assert.False(response.RootElement.GetProperty("alreadyApplied").GetBoolean());
        Assert.Equal(2, response.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal(Hash(firstAfter), response.RootElement.GetProperty("files")[0].GetProperty("resultHash").GetString());
        Assert.Equal(Hash(secondAfter), response.RootElement.GetProperty("files")[1].GetProperty("resultHash").GetString());
    }

    [Fact]
    public async Task Execute_WaitsForActiveAgentSessionThenAppliesApprovedPatch()
    {
        await using var repo = await RepositoryFixture.CreateAsync(nestedProject: false);
        const string path = "docs/guide.md";
        const string before = "Before.\n";
        const string after = "After.\n";
        await repo.WriteCommittedAsync(path, before);
        var patch = await repo.CreatePatchAsync(path, after);
        var payload = Payload(repo.ProjectId, patch, path, before, after);
        var actualRepositoryRoot = (await Git(repo.ProjectRoot, "rev-parse", "--show-toplevel")).Trim();
        var session = GitRepositoryOperationGate.RegisterSession(actualRepositoryRoot);
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressReports = 0;

        var execution = _service.ExecuteAsync(
            repo.ProjectRoot,
            repo.ProjectId,
            payload,
            CancellationToken.None,
            (activity, _) =>
            {
                Assert.Contains("Waiting for the current agent session", activity, StringComparison.Ordinal);
                Interlocked.Increment(ref progressReports);
                waiting.TrySetResult(true);
                return Task.CompletedTask;
            });

        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(execution.IsCompleted);
            Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path)));
        }
        finally
        {
            session.Dispose();
        }

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Ok, result.Error);
        Assert.True(progressReports >= 1);
        Assert.Equal(after, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path)));
    }

    [Fact]
    public async Task Execute_RollsBackWhenManifestContainsAPathOutsideTheDocumentationGrant()
    {
        await using var repo = await RepositoryFixture.CreateAsync(nestedProject: false);
        const string path = "src/unsafe.md";
        const string before = "Before.\n";
        const string after = "After.\n";
        await repo.WriteCommittedAsync(path, before);
        var patch = await repo.CreatePatchAsync(path, after);
        var payload = Payload(repo.ProjectId, patch, path, before, after, allowedPaths: ["docs"]);

        var result = await _service.ExecuteAsync(repo.ProjectRoot, repo.ProjectId, payload, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("InvalidPayload", result.Error);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(repo.ProjectRoot, path)));
    }

    private static string Payload(
        Guid projectId,
        string patch,
        string projectPath,
        string before,
        string after,
        string? gitPath = null,
        string[]? allowedPaths = null,
        Guid? proposalId = null) =>
        JsonSerializer.Serialize(new
        {
            projectId,
            proposalId = proposalId ?? Guid.NewGuid(),
            patch,
            files = new[]
            {
                new
                {
                    gitPath = gitPath ?? projectPath,
                    projectPath,
                    baseHash = Hash(before),
                    resultHash = Hash(after)
                }
            },
            allowedPaths = allowedPaths ?? ["docs"]
        });

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RepositoryFixture : IAsyncDisposable
    {
        private RepositoryFixture(string parent, string repositoryRoot, string projectRoot, Guid projectId)
        {
            Parent = parent;
            RepositoryRoot = repositoryRoot;
            ProjectRoot = projectRoot;
            ProjectId = projectId;
            GitPrefix = projectRoot == repositoryRoot
                ? string.Empty
                : Path.GetRelativePath(repositoryRoot, projectRoot).Replace(Path.DirectorySeparatorChar, '/') + "/";
        }

        public string Parent { get; }
        public string RepositoryRoot { get; }
        public string ProjectRoot { get; }
        public Guid ProjectId { get; }
        public Guid ProposalId { get; } = Guid.NewGuid();
        public string GitPrefix { get; }

        public static async Task<RepositoryFixture> CreateAsync(bool nestedProject)
        {
            var parent = Path.Combine(Path.GetTempPath(), "beacon-repo-patch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(parent);
            var repositoryRoot = Path.Combine(parent, "repository");
            Directory.CreateDirectory(repositoryRoot);
            var projectRoot = nestedProject
                ? Path.Combine(repositoryRoot, "sub", "project")
                : repositoryRoot;
            Directory.CreateDirectory(projectRoot);
            var fixture = new RepositoryFixture(parent, repositoryRoot, projectRoot, Guid.NewGuid());
            try
            {
                await Git(repositoryRoot, "init");
                // Keep fixture bytes stable on Windows; production hashes still cover exact checkout bytes.
                await Git(repositoryRoot, "config", "core.autocrlf", "false");
                await Git(repositoryRoot, "config", "core.eol", "lf");
                await Git(repositoryRoot, "config", "user.name", "ProjectBeacon Tests");
                await Git(repositoryRoot, "config", "user.email", "tests@projectbeacon.local");
                await Git(repositoryRoot, "config", "commit.gpgsign", "false");
                await File.WriteAllTextAsync(Path.Combine(repositoryRoot, ".gitignore"), "bin/\nobj/\n");
                await Git(repositoryRoot, "add", ".gitignore");
                await Git(repositoryRoot, "commit", "-m", "initial");
                return fixture;
            }
            catch
            {
                try { Directory.Delete(parent, recursive: true); } catch { }
                throw;
            }
        }

        public async Task WriteCommittedAsync(string projectRelativePath, string content)
        {
            var path = Path.Combine(ProjectRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            await Git(RepositoryRoot, "add", GitPrefix + projectRelativePath);
            await Git(RepositoryRoot, "commit", "-m", "add documentation");
        }

        public async Task<string> CreatePatchAsync(string projectRelativePath, string proposedContent)
        {
            var path = Path.Combine(ProjectRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllTextAsync(path, proposedContent);
            var patch = await Git(RepositoryRoot, "diff", "--no-ext-diff", "--", GitPrefix + projectRelativePath);
            Assert.False(string.IsNullOrWhiteSpace(patch));

            // Build the proposal from the diff, but leave the fixture at the immutable
            // base revision. The apply service must verify BaseHash before running git apply.
            await Git(RepositoryRoot, "checkout", "--", GitPrefix + projectRelativePath);
            return patch + "\n";
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Parent, recursive: true); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<string> Git(string workingDirectory, params string[] arguments)
    {
        using var process = new Process();
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        process.StartInfo = start;
        if (!process.Start())
            throw new InvalidOperationException("Could not start Git in the RepoPatch test.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw new TimeoutException($"git {string.Join(' ', arguments)} timed out.");
        }

        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
        return output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }
}
