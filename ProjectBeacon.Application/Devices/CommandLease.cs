namespace ProjectBeacon.Application.Devices;

using Domain.Enums;

/// <summary>
/// Execution lease granted at claim, per command kind. Every current kind is hard-capped
/// client-side (LLM/eval turns 180s, review check and winget install 120s), so a 15-minute
/// lease covers all of them with margin. The exhaustive switch forces an explicit lease
/// whenever a new kind is added.
/// </summary>
public static class CommandLease
{
    public const int Minutes = 15;

    // CS8524 warns about "unnamed" enum values beyond the declared members even when every
    // member is covered; every named kind has an explicit arm on purpose.
#pragma warning disable CS8524
    public static TimeSpan For(WorkstationCommandKind kind) => kind switch
    {
        WorkstationCommandKind.Probe => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ListDir => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.Install => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.InitProject => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ApplyOpencode => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ScanGguf => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ReloadProxy => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.UnloadProxy => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.SwapModel => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.SaveWorkstation => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ChatEnsureSession => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ChatPrompt => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ChatAbort => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ConfigureOpenCode => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.RunEvalTurn => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ReconcileDesired => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.RunReviewCheck => TimeSpan.FromMinutes(Minutes)
    };
}
