namespace ProjectBeacon.DockerLogProxy;

using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ProjectBeacon.Application.Operations;

public sealed class DockerEngineLogReader(
    HttpClient dockerClient,
    DockerLogProxyOptions options,
    TimeProvider timeProvider,
    ILogger<DockerEngineLogReader> logger)
{
    private const string DockerApiVersion = "v1.41";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PlatformContainerLogResponse?> ReadAsync(
        PlatformLogTarget target,
        int tail,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target));
        if (tail is < 1 || tail > options.MaximumTail)
            throw new ArgumentOutOfRangeException(nameof(tail));

        var serviceName = target switch
        {
            PlatformLogTarget.Web => "web",
            PlatformLogTarget.Worker => "worker",
            PlatformLogTarget.Migrate => "migrate",
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        using var listResponse = await dockerClient.GetAsync(
            $"/{DockerApiVersion}/containers/json?all=1",
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (!listResponse.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Docker Engine container listing failed with HTTP {(int)listResponse.StatusCode}.");

        var containers = await listResponse.Content.ReadFromJsonAsync<DockerContainerSummary[]>(
            JsonOptions, ct) ?? [];
        var candidates = containers
            .Where(container => IsAllowedContainer(container, serviceName))
            .OrderByDescending(container => string.Equals(container.State, "running", StringComparison.Ordinal))
            .ThenByDescending(container => container.Created)
            .Take(options.MaximumContainers)
            .ToList();

        if (candidates.Count == 0)
            return null;

        var entries = new List<PlatformContainerLogEntry>(tail);
        var names = new List<string>(candidates.Count);
        foreach (var container in candidates)
        {
            ct.ThrowIfCancellationRequested();
            names.Add(container.Names?.FirstOrDefault()?.TrimStart('/') ?? container.Id);
            var remaining = tail - entries.Count;
            if (remaining <= 0)
                break;

            var path = $"/{DockerApiVersion}/containers/{container.Id}/logs"
                + $"?stdout=1&stderr=1&timestamps=1&tail={remaining.ToString(CultureInfo.InvariantCulture)}";
            using var response = await dockerClient.GetAsync(
                path, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                continue;
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Docker Engine log retrieval for allowlisted service {Service} returned HTTP {StatusCode}.",
                    serviceName,
                    (int)response.StatusCode);
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var fileEntries = await ReadMultiplexedStreamAsync(stream, remaining, ct);
            entries.AddRange(fileEntries);
        }

        var ordered = entries
            .OrderBy(entry => entry.TimestampUtc)
            .TakeLast(tail)
            .ToArray();

        return new PlatformContainerLogResponse(
            target,
            string.Join(", ", names.Distinct(StringComparer.Ordinal)),
            timeProvider.GetUtcNow(),
            ordered.Length,
            ordered);
    }

    private bool IsAllowedContainer(DockerContainerSummary container, string serviceName)
    {
        if (!IsContainerId(container.Id))
            return false;
        if (container.Labels is null)
            return false;

        return container.Labels.TryGetValue("com.docker.compose.project", out var project)
            && string.Equals(project, options.ComposeProject, StringComparison.Ordinal)
            && container.Labels.TryGetValue("com.docker.compose.service", out var service)
            && string.Equals(service, serviceName, StringComparison.Ordinal);
    }

    private static bool IsContainerId(string? id) =>
        id is { Length: >= 12 and <= 64 }
        && id.All(Uri.IsHexDigit);

    private async Task<IReadOnlyList<PlatformContainerLogEntry>> ReadMultiplexedStreamAsync(
        Stream stream,
        int maximumLines,
        CancellationToken ct)
    {
        var entries = new Queue<PlatformContainerLogEntry>(maximumLines);
        var header = new byte[8];
        var totalBytes = 0;

        while (true)
        {
            var headerBytes = await ReadFrameHeaderAsync(stream, header, ct);
            if (headerBytes == 0)
                break;
            if (headerBytes != header.Length)
                throw new InvalidDataException("Docker Engine returned a truncated log frame header.");

            var streamType = header[0];
            if (streamType is not (1 or 2) || header[1] != 0 || header[2] != 0 || header[3] != 0)
                throw new InvalidDataException("Docker Engine returned an unsupported log stream frame.");

            var frameLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
            if (frameLength == 0
                || frameLength > (uint)options.MaximumFrameBytes
                || totalBytes + (long)frameLength > options.MaximumResponseBytes)
                throw new InvalidDataException("Docker Engine log frame exceeded the configured size limit.");

            var payload = new byte[(int)frameLength];
            await stream.ReadExactlyAsync(payload, ct);
            totalBytes += payload.Length;
            var text = Encoding.UTF8.GetString(payload);
            var streamName = streamType == 2 ? "stderr" : "stdout";
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = ParseTimestampedLine(line, streamName);
                if (entry is null)
                    continue;
                if (entries.Count == maximumLines)
                    entries.Dequeue();
                entries.Enqueue(entry);
            }
        }

        return entries.ToArray();
    }

    private static async Task<int> ReadFrameHeaderAsync(
        Stream stream,
        Memory<byte> header,
        CancellationToken ct)
    {
        var total = 0;
        while (total < header.Length)
        {
            var read = await stream.ReadAsync(header[total..], ct);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private static PlatformContainerLogEntry? ParseTimestampedLine(string line, string stream)
    {
        line = line.TrimEnd('\r');
        var separator = line.IndexOf(' ');
        if (separator <= 0)
            return null;

        var timestampText = line[..separator];
        var fractionalSeparator = timestampText.IndexOf('.');
        var utcSuffix = timestampText.IndexOf('Z', fractionalSeparator >= 0 ? fractionalSeparator : 0);
        if (fractionalSeparator >= 0 && utcSuffix > fractionalSeparator + 8)
        {
            // Docker emits RFC3339Nano (up to 9 fractional digits); DateTimeOffset
            // preserves 7 digits, so truncate only the sub-100ns portion.
            timestampText = timestampText[..(fractionalSeparator + 8)] + "Z";
        }

        if (!DateTimeOffset.TryParse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var timestamp))
            return null;

        var message = line[(separator + 1)..];
        return new PlatformContainerLogEntry(timestamp.ToUniversalTime(), stream, message);
    }

    private sealed record DockerContainerSummary(
        string Id,
        string[]? Names,
        string State,
        long Created,
        Dictionary<string, string>? Labels);
}
