namespace ProjectBeacon.Web.Startup;

using Microsoft.Extensions.Logging;

public class DockerPostgresConfig
{
    public string? PostgresPassword { get; set; }
    public int Port { get; set; } = 5432;
    public string ContainerName { get; set; } = "beacon-postgres";
    public string Image { get; set; } = "postgres:18-bookworm";
    public string Volume { get; set; } = "pgdata";
}

public class DockerPostgresHelper
{
    private readonly ILogger<DockerPostgresHelper> _logger;
    private const string Database = "beacon";
    private const string User = "beacon";

    public DockerPostgresHelper(ILogger<DockerPostgresHelper> logger)
    {
        _logger = logger;
    }

    public bool EnsureRunning(DockerPostgresConfig config)
    {
        try
        {
            var status = RunDockerCommand($"ps --filter name={config.ContainerName} --format {{{{.Status}}}}");
            if (!string.IsNullOrEmpty(status) && status.Contains("Up"))
            {
                _logger.LogInformation("Docker container '{Container}' is already running.", config.ContainerName);
                return WaitForReady(config);
            }

            _logger.LogInformation("Docker container '{Container}' not running. Starting...", config.ContainerName);

            var password = config.PostgresPassword ?? "beacon";

            RunDockerCommand(
                $"run --name {config.ContainerName} -d " +
                $"-p {config.Port}:5432 " +
                $"-e POSTGRES_USER={User} " +
                $"-e POSTGRES_PASSWORD={password} " +
                $"-e POSTGRES_DB={Database} " +
                $"-v {config.Volume}:/var/lib/postgresql/data " +
                $"--healthcheck 'CMD-SHELL pg_isready -U {User} -d {Database}' " +
                $"--health-interval 5s --health-timeout 5s --health-retries 10 " +
                $"{config.Image}");

            _logger.LogInformation("Docker container '{Container}' started.", config.ContainerName);
            return WaitForReady(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure Docker container '{Container}' is running.", config.ContainerName);
            return false;
        }
    }

    private bool WaitForReady(DockerPostgresConfig config, int maxRetries = 12, int delayMs = 5000)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            var status = RunDockerCommand($"inspect --format={{{{.State.Status}}}} {config.ContainerName}");
            if (status?.Trim() == "running")
            {
                var health = RunDockerCommand($"inspect --format={{{{.State.Health.Status}}}} {config.ContainerName}");
                if (health?.Trim() == "healthy")
                {
                    _logger.LogInformation("Docker container '{Container}' is healthy.", config.ContainerName);
                    return true;
                }

                if (string.IsNullOrEmpty(health) || health.Trim() == "none")
                {
                    _logger.LogInformation("Docker container '{Container}' is running (no healthcheck configured).", config.ContainerName);
                    return true;
                }
            }

            _logger.LogInformation("Waiting for Docker container '{Container}' (attempt {Attempt}/{Max})...", config.ContainerName, i + 1, maxRetries);
            Thread.Sleep(delayMs);
        }

        return false;
    }

    private static string? RunDockerCommand(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit();
            var stdout = process?.StandardOutput.ReadToEnd()?.Trim();

            if (process?.ExitCode == 0)
            {
                return stdout;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
