namespace ProjectBeacon.Application.Operations;

using ProjectBeacon.Application.Admin;
using ProjectBeacon.Application.Common;

public enum PlatformLogTarget
{
    Web = 0,
    Worker = 1,
    Migrate = 2
}

public sealed record PlatformContainerLogEntry(
    DateTimeOffset TimestampUtc,
    string Stream,
    string Message);

public sealed record PlatformContainerLogResponse(
    PlatformLogTarget Service,
    string ContainerName,
    DateTimeOffset CollectedAtUtc,
    int ReturnedLines,
    IReadOnlyList<PlatformContainerLogEntry> Entries);

public interface IPlatformContainerLogClient
{
    Task<Result<PlatformContainerLogResponse>> GetLogsAsync(
        AdminActor actor,
        PlatformLogTarget service,
        int tail = 200,
        CancellationToken ct = default);
}
