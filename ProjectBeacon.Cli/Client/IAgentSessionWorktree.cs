namespace ProjectBeacon.Cli.Client;

internal interface IAgentSessionWorktree : IAsyncDisposable
{
    string WorkingDirectory { get; }
    string ProjectRoot { get; }
    string RecoveryPath { get; }
    string BuildEnvironmentContract();
    void Preserve();
    Task<string> BuildDiffArtifactAsync(CancellationToken ct);
    Task<(bool Success, string? Error, string? CommitSha)> CompleteChangesAsync(
        string commitMessage,
        CancellationToken ct);
}
