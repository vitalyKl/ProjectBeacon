namespace ProjectBeacon.OpsAgent;

using System.Globalization;
using System.Text.Json;
using ProjectBeacon.Application.Operations;

public sealed class DockerJsonLogReader(
    OpsAgentOptions options,
    TimeProvider timeProvider,
    ILogger<DockerJsonLogReader> logger)
{
    private const int MaximumContainerIdLength = 64;
    private const int MinimumContainerIdLength = 12;
    private const int MaximumLogLineCharacters = 64 * 1024;
    private static readonly string[] RotationSuffixes = [".log", ".log.1", ".log.2"];

    public async Task<PlatformContainerLogResponse?> ReadAsync(
        PlatformLogTarget target,
        int tail,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));
        if (tail is < 1 || tail > options.MaximumTail)
            throw new ArgumentOutOfRangeException(nameof(tail));

        if (!Directory.Exists(options.DockerLogRoot))
            return null;

        var expectedService = target switch
        {
            PlatformLogTarget.Web => "web",
            PlatformLogTarget.Worker => "worker",
            PlatformLogTarget.Migrate => "migrate",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        var containers = FindContainers(expectedService);
        if (containers.Count == 0)
            return null;

        var entries = new List<PlatformContainerLogEntry>(tail);
        foreach (var container in containers)
        {
            foreach (var suffix in RotationSuffixes)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = tail - entries.Count;
                if (remaining <= 0)
                    break;

                var path = Path.Combine(container.DirectoryPath, container.Id + "-json" + suffix);
                if (!IsSafeLogFile(container.DirectoryPath, path))
                    continue;

                try
                {
                    var fileEntries = await ReadTailFromFileAsync(path, remaining, ct);
                    entries.AddRange(fileEntries);
                }
                catch (IOException exception)
                {
                    logger.LogDebug(exception, "Could not read a rotated log file for an allowlisted container.");
                }
                catch (UnauthorizedAccessException exception)
                {
                    logger.LogWarning(exception, "OpsAgent cannot read an allowlisted container log file.");
                }
            }

            if (entries.Count >= tail)
                break;
        }

        var ordered = entries
            .OrderBy(entry => entry.TimestampUtc)
            .TakeLast(tail)
            .ToArray();

        return new PlatformContainerLogResponse(
            target,
            string.Join(", ", containers.Select(container => container.Name).Distinct(StringComparer.Ordinal)),
            timeProvider.GetUtcNow(),
            ordered.Length,
            ordered);
    }

    private List<ContainerMetadata> FindContainers(string expectedService)
    {
        var candidates = new List<ContainerMetadata>();
        foreach (var directory in Directory.EnumerateDirectories(options.DockerLogRoot))
        {
            var id = Path.GetFileName(directory);
            if (!IsContainerId(id))
                continue;

            var metadataPath = Path.Combine(directory, "config.v2.json");
            if (!File.Exists(metadataPath))
                continue;

            try
            {
                var file = new FileInfo(metadataPath);
                if (file.Length is <= 0 or > 1024 * 1024 || file.LinkTarget is not null)
                    continue;

                using var document = JsonDocument.Parse(File.ReadAllBytes(metadataPath), new JsonDocumentOptions
                {
                    MaxDepth = 16
                });
                var root = document.RootElement;
                var labels = root.TryGetProperty("Config", out var config)
                    && config.TryGetProperty("Labels", out var labelElement)
                    ? labelElement
                    : default;
                if (labels.ValueKind != JsonValueKind.Object)
                    continue;

                var service = GetLabel(labels, "com.docker.compose.service");
                var project = GetLabel(labels, "com.docker.compose.project");
                if (!string.Equals(service, expectedService, StringComparison.Ordinal)
                    || !string.Equals(project, options.ComposeProject, StringComparison.Ordinal))
                    continue;

                var name = root.TryGetProperty("Name", out var nameElement)
                    ? nameElement.GetString()?.TrimStart('/') ?? service
                    : service;
                var isRunning = root.TryGetProperty("State", out var state)
                    && state.TryGetProperty("Running", out var running)
                    && running.ValueKind == JsonValueKind.True;
                var currentLog = Path.Combine(directory, id + "-json.log");
                var lastChanged = File.Exists(currentLog)
                    ? File.GetLastWriteTimeUtc(currentLog)
                    : Directory.GetLastWriteTimeUtc(directory);
                candidates.Add(new ContainerMetadata(
                    id,
                    directory,
                    name,
                    isRunning,
                    lastChanged));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                logger.LogDebug(exception, "Skipping unreadable Docker container metadata in OpsAgent.");
            }
        }

        return candidates
            .OrderByDescending(container => container.IsRunning)
            .ThenByDescending(container => container.LastChangedUtc)
            .Take(options.MaximumContainers)
            .ToList();
    }

    private static string? GetLabel(JsonElement labels, string name) =>
        labels.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsContainerId(string id) =>
        id.Length is >= MinimumContainerIdLength and <= MaximumContainerIdLength
        && id.All(Uri.IsHexDigit);

    private static bool IsSafeLogFile(string containerDirectory, string logPath)
    {
        var root = Path.GetFullPath(containerDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(logPath);
        if (!fullPath.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            return false;

        if (!File.Exists(fullPath))
            return false;

        var info = new FileInfo(fullPath);
        return info.LinkTarget is null && (info.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private async Task<IReadOnlyList<PlatformContainerLogEntry>> ReadTailFromFileAsync(
        string path,
        int maximumLines,
        CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (file.Length <= 0)
            return [];

        var results = new Queue<PlatformContainerLogEntry>(maximumLines);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        var startOffset = Math.Max(0, stream.Length - options.MaximumLogFileBytes);
        if (startOffset > 0)
            stream.Seek(startOffset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        if (startOffset > 0)
            await reader.ReadLineAsync(ct); // Discard the partial first JSON record.

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length is 0 or > MaximumLogLineCharacters)
                continue;

            var entry = ParseLine(line);
            if (entry is null)
                continue;

            if (results.Count == maximumLines)
                results.Dequeue();
            results.Enqueue(entry);
        }

        return results.ToArray();
    }

    private static PlatformContainerLogEntry? ParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!root.TryGetProperty("log", out var logElement)
                || logElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("time", out var timeElement)
                || timeElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    timeElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var timestamp))
                return null;

            var stream = root.TryGetProperty("stream", out var streamElement)
                && streamElement.ValueKind == JsonValueKind.String
                    ? streamElement.GetString()
                    : "unknown";
            if (stream is not ("stdout" or "stderr"))
                stream = "unknown";

            var message = logElement.GetString() ?? string.Empty;
            return new PlatformContainerLogEntry(
                timestamp.ToUniversalTime(),
                stream!,
                message.TrimEnd('\r', '\n'));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ContainerMetadata(
        string Id,
        string DirectoryPath,
        string Name,
        bool IsRunning,
        DateTime LastChangedUtc);
}
