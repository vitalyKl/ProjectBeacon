namespace ProjectBeacon.Application.Devices;

using Domain.Enums;

/// <summary>
/// Execution lease granted at claim. The duration is declared with the command metadata so
/// adding a command requires one catalog entry rather than a second exhaustive switch.
/// </summary>
public static class CommandLease
{
    public const int Minutes = 15;
    public const int PipelineMinutes = 75;

    public static TimeSpan For(WorkstationCommandKind kind) =>
        WorkstationCommandCatalog.Get(kind).LeaseDuration;
}
