namespace ProjectBeacon.Cli.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ProjectBeacon.Cli.Client;

public sealed class AgentSessionWorktreeTests
{
    [Fact]
    public async Task CreateAndApply_UsesIsolatedWorktreeWithoutChangingOriginalIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-agent-worktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init", "-b", "main");
            await Git(root, "config", "user.name", "ProjectBeacon Tests");
            await Git(root, "config", "user.email", "tests@projectbeacon.local");
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "base\n");
            await Git(root, "add", "README.md");
            await Git(root, "commit", "-m", "initial");

            // Simulate pre-existing user work in all three Git states.
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "user unstaged edit\n");
            await File.WriteAllTextAsync(Path.Combine(root, "staged.txt"), "user staged file\n");
            await Git(root, "add", "staged.txt");
            await File.WriteAllTextAsync(Path.Combine(root, "untracked.txt"), "user untracked file\n");

            var originalIndexHash = HashFile(Path.Combine(root, ".git", "index"));

            var created = await AgentSessionWorktree.CreateAsync(root, Guid.NewGuid(), CancellationToken.None);
            Assert.NotNull(created.Workspace);
            Assert.Null(created.Error);
            var workspace = created.Workspace!;
            var recoveryPath = workspace.RecoveryPath;

            Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(workspace.WorkingDirectory));
            Assert.Equal("user unstaged edit\n", await File.ReadAllTextAsync(Path.Combine(workspace.WorkingDirectory, "README.md")));
            Assert.Equal("user staged file\n", await File.ReadAllTextAsync(Path.Combine(workspace.WorkingDirectory, "staged.txt")));
            Assert.Equal("user untracked file\n", await File.ReadAllTextAsync(Path.Combine(workspace.WorkingDirectory, "untracked.txt")));

            await File.WriteAllTextAsync(Path.Combine(workspace.WorkingDirectory, "agent-output.txt"), "verified by agent\n");
            await File.AppendAllTextAsync(Path.Combine(workspace.WorkingDirectory, "README.md"), "agent appended\n");

            var applied = await workspace.ApplyChangesAsync(CancellationToken.None);
            Assert.True(applied.Success, applied.Error);

            Assert.Equal("user unstaged edit\nagent appended\n", await File.ReadAllTextAsync(Path.Combine(root, "README.md")));
            Assert.Equal("user staged file\n", await File.ReadAllTextAsync(Path.Combine(root, "staged.txt")));
            Assert.Equal("user untracked file\n", await File.ReadAllTextAsync(Path.Combine(root, "untracked.txt")));
            Assert.Equal("verified by agent\n", await File.ReadAllTextAsync(Path.Combine(root, "agent-output.txt")));

            // Patch-back changes only the working tree; the user's staging area is untouched.
            Assert.Equal(originalIndexHash, HashFile(Path.Combine(root, ".git", "index")));
            var actualStatus = await Git(root, "status", "--porcelain", "--untracked-files=all");
            Assert.Contains(" M README.md", actualStatus, StringComparison.Ordinal);
            Assert.Contains("A  staged.txt", actualStatus, StringComparison.Ordinal);
            Assert.Contains("?? untracked.txt", actualStatus, StringComparison.Ordinal);
            Assert.Contains("?? agent-output.txt", actualStatus, StringComparison.Ordinal);

            await workspace.DisposeAsync();
            Assert.False(Directory.Exists(recoveryPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string HashFile(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start Git in the worktree test.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
        return output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }
}
