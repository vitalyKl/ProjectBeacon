namespace ProjectBeacon.Cli.Client;

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    public static async Task<UpdateInfo> DownloadAndScheduleAsync(CancellationToken ct = default)
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
        var info = new UpdateInfo(current.ToString(4), latest.ToString(4), latest > current, rid, releaseUrl);
        if (!info.UpdateAvailable)
            return info;

        var assets = root.GetProperty("assets").EnumerateArray()
            .ToDictionary(a => a.GetProperty("name").GetString()!, a => a.GetProperty("browser_download_url").GetString()!, StringComparer.Ordinal);
        var archiveName = $"beacon-client-{rid}.zip";
        if (!assets.TryGetValue(archiveName, out var archiveUrl) || !assets.TryGetValue("SHA256SUMS.txt", out var sumsUrl))
            throw new InvalidDataException($"The release does not contain {archiveName} and SHA256SUMS.txt.");

        var workDir = Path.Combine(Path.GetTempPath(), "ProjectBeacon", "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var sums = await http.GetStringAsync(sumsUrl, ct);
            var expectedHash = FindChecksum(sums, archiveName);
            var archive = await http.GetByteArrayAsync(archiveUrl, ct);
            var actualHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHash), Encoding.ASCII.GetBytes(actualHash)))
                throw new CryptographicException("The downloaded client archive failed SHA-256 verification.");

            var archivePath = Path.Combine(workDir, archiveName);
            await File.WriteAllBytesAsync(archivePath, archive, ct);
            var executableName = OperatingSystem.IsWindows() ? "beacon.exe" : "beacon";
            var newExecutable = Path.Combine(workDir, executableName);
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var entry = zip.Entries.SingleOrDefault(e => string.Equals(e.FullName, executableName, StringComparison.Ordinal));
                if (entry is null)
                    throw new InvalidDataException($"The client archive does not contain {executableName} at its root.");
                entry.ExtractToFile(newExecutable, overwrite: true);
            }

            var currentExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExecutable)
                || !string.Equals(Path.GetFileName(currentExecutable), executableName,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Self-update requires the published single-file 'beacon' executable. Start the installed client instead of using dotnet run.");

            EnsureWritableDirectory(Path.GetDirectoryName(currentExecutable)!);
            ScheduleReplacement(currentExecutable, newExecutable, workDir);
            return info;
        }
        catch
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
            throw;
        }
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
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(scriptPath, """
                param([int]$targetPid, [string]$target, [string]$source, [string]$workDir)
                $ErrorActionPreference = 'Stop'
                try {
                    Wait-Process -Id $targetPid -ErrorAction SilentlyContinue
                    $backup = "$target.bak"
                    if (Test-Path $backup) { Remove-Item $backup -Force }
                    Copy-Item $target $backup -Force
                    try {
                        Copy-Item $source $target -Force
                        $newProcess = Start-Process -FilePath $target -ArgumentList @('client', '--headless') -WorkingDirectory (Split-Path $target -Parent) -PassThru
                        Start-Sleep -Seconds 3
                        if ($newProcess.HasExited) { throw 'Updated client exited during startup.' }
                    } catch {
                        Copy-Item $backup $target -Force
                        Start-Process -FilePath $target -ArgumentList @('client', '--headless') -WorkingDirectory (Split-Path $target -Parent)
                        throw
                    }
                    Remove-Item $backup -Force -ErrorAction SilentlyContinue
                } finally {
                    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
                }
                """);
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                Environment.ProcessId.ToString(), target, source, workDir })
                start.ArgumentList.Add(arg);
            Process.Start(start) ?? throw new InvalidOperationException("Could not start the update helper process.");
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
                while kill -0 "$target_pid" 2>/dev/null; do sleep 1; done
                backup="$target.bak"
                cp -p "$target" "$backup"
                if ! cp "$source" "$target"; then mv "$backup" "$target"; exit 1; fi
                chmod 755 "$target"
                nohup "$target" client --headless >/dev/null 2>&1 &
                new_pid=$!
                sleep 3
                if ! kill -0 "$new_pid" 2>/dev/null; then
                    mv "$backup" "$target"
                    nohup "$target" client --headless >/dev/null 2>&1 &
                    exit 1
                fi
                rm -f "$backup"
                rm -rf "$work_dir"
                """);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { scriptPath, Environment.ProcessId.ToString(), target, source, workDir })
                start.ArgumentList.Add(arg);
            Process.Start(start) ?? throw new InvalidOperationException("Could not start the update helper process.");
        }
    }
}
