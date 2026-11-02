namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record ClientUpdateProgress(
    string Stage,
    string Message,
    double? Percent,
    long BytesDownloaded = 0,
    long? TotalBytes = null);

public static class ClientUpdater
{
    private const string Repository = "vitalyKl/ProjectBeacon";
    private const string ReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    public sealed record UpdateInfo(string CurrentVersion, string LatestVersion, bool UpdateAvailable, string RuntimeIdentifier, string ReleaseUrl);

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        using var http = CreateHttpClient();
        using var response = await http.GetAsync(ReleaseApi, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("The latest release has no tag_name.");
        if (!Version.TryParse(VersionPart(tag), out var parsedLatest))
            throw new InvalidDataException($"Release tag '{tag}' is not a valid version.");
        var latest = Normalize(parsedLatest);
        var current = Normalize(typeof(ClientUpdater).Assembly.GetName().Version ?? new Version(0, 0));
        var rid = GetRuntimeIdentifier();
        var releaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "";
        var assetNames = root.GetProperty("assets").EnumerateArray()
            .Select(asset => asset.GetProperty("name").GetString())
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        var hasPackage = assetNames.Contains($"beacon-client-{rid}.zip") && assetNames.Contains("SHA256SUMS.txt");
        return new UpdateInfo(current.ToString(4), latest.ToString(4), latest > current && hasPackage, rid, releaseUrl);
    }

    public static Task<UpdateInfo> DownloadAndScheduleAsync(CancellationToken ct = default) =>
        DownloadAndScheduleAsync(null, ct);

    public static async Task<UpdateInfo> DownloadAndScheduleAsync(
        Func<ClientUpdateProgress, CancellationToken, Task>? reportProgress,
        CancellationToken ct = default)
    {
        await ReportProgressAsync(reportProgress,
            new ClientUpdateProgress("check", "Checking latest client release", 0), ct);

        using var http = CreateHttpClient();
        using var response = await http.GetAsync(ReleaseApi, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidDataException("The latest release has no tag_name.");
        if (!Version.TryParse(VersionPart(tag), out var parsedLatest))
            throw new InvalidDataException($"Release tag '{tag}' is not a valid version.");

        var latest = Normalize(parsedLatest);
        var current = Normalize(typeof(ClientUpdater).Assembly.GetName().Version ?? new Version(0, 0));
        var rid = GetRuntimeIdentifier();
        var releaseUrl = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "";
        var assets = root.GetProperty("assets").EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("browser_download_url").GetString()!, StringComparer.Ordinal);
        var archiveName = $"beacon-client-{rid}.zip";
        var hasPackage = assets.ContainsKey(archiveName) && assets.ContainsKey("SHA256SUMS.txt");
        var info = new UpdateInfo(current.ToString(4), latest.ToString(4), latest > current && hasPackage, rid, releaseUrl);
        if (!info.UpdateAvailable)
        {
            await ReportProgressAsync(reportProgress,
                new ClientUpdateProgress("complete", "The client is already up to date", 100), ct);
            return info;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            await ReportProgressAsync(reportProgress,
                new ClientUpdateProgress("checksum", "Downloading checksum manifest", 4), ct);
            var sums = await http.GetStringAsync(assets["SHA256SUMS.txt"], ct);
            var expectedHash = FindChecksum(sums, archiveName);

            var archivePath = Path.Combine(workDir, archiveName);
            await DownloadAssetAsync(http, assets[archiveName], archivePath, reportProgress, ct);

            await ReportProgressAsync(reportProgress,
                new ClientUpdateProgress("verify", "Verifying SHA-256 checksum", 88), ct);
            await using var archiveStream = File.OpenRead(archivePath);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, ct)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedHash), Encoding.ASCII.GetBytes(actualHash)))
                throw new CryptographicException("The downloaded client archive failed SHA-256 verification.");

            await ReportProgressAsync(reportProgress,
                new ClientUpdateProgress("extract", "Extracting verified client executable", 93), ct);
            var executableName = OperatingSystem.IsWindows() ? "beacon.exe" : "beacon";
            var newExecutable = Path.Combine(workDir, executableName);
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var entry = zip.Entries.SingleOrDefault(e =>
                    string.Equals(e.FullName, executableName, StringComparison.Ordinal));
                if (entry is null)
                    throw new InvalidDataException($"The client archive does not contain {executableName} at its root.");
                entry.ExtractToFile(newExecutable, overwrite: true);
            }

            var currentExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExecutable)
                || !string.Equals(Path.GetFileName(currentExecutable), executableName,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Self-update requires the published single-file 'beacon' executable. Start the installed client instead of using dotnet run.");

            EnsureWritableDirectory(Path.GetDirectoryName(currentExecutable)!);
            await ReportProgressAsync(reportProgress,
                new ClientUpdateProgress("restart", "Preparing automatic restart", 97), ct);
            ScheduleReplacement(currentExecutable, newExecutable, workDir);
            await ReportProgressAsync(reportProgress,
                new ClientUpdateProgress("scheduled", "Update staged; handing off to the automatic restart helper", 100), ct);
            return info;
        }
        catch (Exception ex)
        {
            try
            {
                await ReportProgressAsync(reportProgress,
                    new ClientUpdateProgress("failed", $"Update failed: {ex.Message}", null), ct);
            }
            catch
            {
                // Preserve the original update failure when reporting failure also fails.
            }
            try { Directory.Delete(workDir, recursive: true); } catch { }
            throw;
        }
    }

    private static async Task DownloadAssetAsync(
        HttpClient http,
        string url,
        string destination,
        Func<ClientUpdateProgress, CancellationToken, Task>? reportProgress,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        long lastUnknownSizeReport = 0;
        var lastPercentBucket = -1;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            double? percent = totalBytes is > 0
                ? 8d + downloaded / (double)totalBytes.Value * 77d
                : null;
            if (percent is { } value)
            {
                var bucket = (int)Math.Floor(value / 2d);
                if (bucket <= lastPercentBucket)
                    continue;
                lastPercentBucket = bucket;
            }
            else
            {
                if (downloaded - lastUnknownSizeReport < 2 * 1024 * 1024)
                    continue;
                lastUnknownSizeReport = downloaded;
            }

            var message = totalBytes is > 0
                ? $"Downloading client archive ({FormatBytes(downloaded)} / {FormatBytes(totalBytes.Value)})"
                : $"Downloading client archive ({FormatBytes(downloaded)})";
            await ReportProgressAsync(reportProgress,
                new ClientUpdateProgress("download", message, percent, downloaded, totalBytes), ct);
        }
    }

    private static Task ReportProgressAsync(
        Func<ClientUpdateProgress, CancellationToken, Task>? reportProgress,
        ClientUpdateProgress progress,
        CancellationToken ct) =>
        reportProgress is null ? Task.CompletedTask : reportProgress(progress, ct);

    private static string FormatBytes(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.0} {units[unit]}";
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectBeacon-Client-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static string VersionPart(string tag) =>
        tag.TrimStart('v', 'V').Split(new[] { '-', '+' }, StringSplitOptions.RemoveEmptyEntries)[0];

    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static void EnsureWritableDirectory(string directory)
    {
        var probe = Path.Combine(directory, ".beacon-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (File.Create(probe)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"The client installation directory '{directory}' is not writable by the current user.", ex);
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }

    private static string GetRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported client architecture: {RuntimeInformation.ProcessArchitecture}.")
        };
        if (OperatingSystem.IsWindows()) return $"win-{architecture}";
        if (OperatingSystem.IsLinux()) return $"linux-{architecture}";
        if (OperatingSystem.IsMacOS()) return $"osx-{architecture}";
        throw new PlatformNotSupportedException("Automatic client updates are supported on Windows, Linux, and macOS.");
    }

    private static string FindChecksum(string manifest, string assetName)
    {
        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[^1].TrimStart('*'), assetName, StringComparison.Ordinal)
                && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        throw new InvalidDataException($"SHA256SUMS.txt has no valid checksum for {assetName}.");
    }

    private static void ScheduleReplacement(string target, string source, string workDir)
    {
        var scriptPath = Path.Combine(workDir, OperatingSystem.IsWindows() ? "replace.ps1" : "replace.sh");
        var restartMode = Environment.GetEnvironmentVariable("BEACON_CLIENT_RESTART_MODE") ?? "headless";
        var logPath = ClientFileLog.LogPath;

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(scriptPath, """
                param([int]$targetPid, [string]$target, [string]$source, [string]$workDir, [string]$restartMode, [string]$logPath)
                $ErrorActionPreference = 'Stop'
                function Write-UpdateLog([string]$message) {
                    try {
                        $directory = Split-Path -Parent $logPath
                        if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
                        Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') updater: $message"
                    } catch { }
                }

                $backup = "$target.bak"
                $arguments = @('client')
                if ($restartMode -ne 'interactive') { $arguments += '--headless' }

                try {
                    Write-UpdateLog "Waiting for process $targetPid to exit."
                    Wait-Process -Id $targetPid -ErrorAction SilentlyContinue
                    if (Test-Path $backup) { Remove-Item $backup -Force }
                    Copy-Item $target $backup -Force
                    Write-UpdateLog "Replacing client executable."
                    Copy-Item $source $target -Force

                    if ($restartMode -eq 'interactive') {
                        $newProcess = Start-Process -FilePath $target -ArgumentList $arguments -WorkingDirectory (Split-Path $target -Parent) -PassThru -NoNewWindow
                    } else {
                        $newProcess = Start-Process -FilePath $target -ArgumentList $arguments -WorkingDirectory (Split-Path $target -Parent) -PassThru -WindowStyle Hidden
                    }
                    Start-Sleep -Seconds 3
                    if ($newProcess.HasExited) { throw "Updated client exited during startup (exit $($newProcess.ExitCode))." }

                    Write-UpdateLog "Updated client started with PID $($newProcess.Id)."
                    Remove-Item $backup -Force -ErrorAction SilentlyContinue
                } catch {
                    Write-UpdateLog "Update failed: $($_.Exception.Message)"
                    try {
                        if (Test-Path $backup) {
                            Copy-Item $backup $target -Force
                            Remove-Item $backup -Force -ErrorAction SilentlyContinue
                        }
                        if ($restartMode -eq 'interactive') {
                            $rollback = Start-Process -FilePath $target -ArgumentList $arguments -WorkingDirectory (Split-Path $target -Parent) -PassThru -NoNewWindow
                        } else {
                            $rollback = Start-Process -FilePath $target -ArgumentList $arguments -WorkingDirectory (Split-Path $target -Parent) -PassThru -WindowStyle Hidden
                        }
                        Write-UpdateLog "Previous client restarted with PID $($rollback.Id)."
                    } catch {
                        Write-UpdateLog "Rollback failed: $($_.Exception.Message)"
                    }
                    throw
                } finally {
                    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
                }
                """);

            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = restartMode != "interactive"
            };
            foreach (var arg in new[] {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                Environment.ProcessId.ToString(), target, source, workDir, restartMode, logPath
            })
                start.ArgumentList.Add(arg);
            var helper = Process.Start(start);
            if (helper is null)
                throw new InvalidOperationException("Could not start the update helper process.");
        }
        else
        {
            File.WriteAllText(scriptPath, """
                #!/bin/sh
                set -eu
                target_pid="$1"
                target="$2"
                source="$3"
                work_dir="$4"
                restart_mode="$5"
                log_path="$6"

                log_line() {
                    mkdir -p "$(dirname "$log_path")" 2>/dev/null || true
                    printf '%s updater: %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$1" >> "$log_path" 2>/dev/null || true
                }

                restart_client() {
                    if [ "$restart_mode" = "interactive" ] && [ -t 0 ] && [ -t 1 ] && [ -e /dev/tty ]; then
                        nohup "$target" client </dev/tty >/dev/tty 2>&1 &
                    else
                        nohup "$target" client --headless >/dev/null 2>&1 &
                    fi
                    echo $!
                }

                log_line "Waiting for process $target_pid to exit."
                while kill -0 "$target_pid" 2>/dev/null; do sleep 1; done

                backup="$target.bak"
                if ! cp -p "$target" "$backup"; then
                    log_line "Could not back up the existing executable; restarting the current client."
                    restart_client >/dev/null 2>&1 || true
                    exit 1
                fi
                log_line "Replacing client executable."
                if ! cp "$source" "$target"; then
                    mv "$backup" "$target"
                    log_line "Copy failed; previous client binary restored."
                    restart_client >/dev/null 2>&1 || true
                    exit 1
                fi
                if ! chmod 755 "$target"; then
                    mv "$backup" "$target"
                    log_line "Could not set executable permissions; previous client binary restored."
                    restart_client >/dev/null 2>&1 || true
                    exit 1
                fi

                new_pid="$(restart_client)"
                sleep 3
                new_state="$(ps -p "$new_pid" -o stat= 2>/dev/null || true)"
                zombie=0
                case "$new_state" in
                    Z*) zombie=1 ;;
                esac
                if ! kill -0 "$new_pid" 2>/dev/null || [ -z "$new_state" ] || [ "$zombie" -eq 1 ]; then
                    log_line "Updated client failed to stay running; rolling back."
                    mv "$backup" "$target"
                    restart_client >/dev/null 2>&1 || true
                    log_line "Previous client binary restored and restart requested."
                    exit 1
                fi

                log_line "Updated client started with PID $new_pid."
                rm -f "$backup"
                rm -rf "$work_dir"
                """);

            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = restartMode != "interactive"
            };
            foreach (var arg in new[] { scriptPath, Environment.ProcessId.ToString(), target, source, workDir, restartMode, logPath })
                start.ArgumentList.Add(arg);
            var helper = Process.Start(start);
            if (helper is null)
                throw new InvalidOperationException("Could not start the update helper process.");
        }
    }
}
