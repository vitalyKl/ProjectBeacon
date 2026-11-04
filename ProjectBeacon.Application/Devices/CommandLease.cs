namespace ProjectBeacon.Application.Devices;

using Domain.Enums;

/// <summary>
/// Execution lease granted at claim, per command kind. Pipeline turns have a dedicated
/// 75-minute lease for a workstation's 60-minute absolute turn cap plus reporting margin.
/// The exhaustive switch forces an explicit lease whenever a new kind is added.
/// </summary>
public static class CommandLease
{
    public const int Minutes = 15;
    public const int PipelineMinutes = 75;

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
        WorkstationCommandKind.ChatSwitchAgent => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ChatReplyForm => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ConfigureOpenCode => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.RunEvalTurn => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.ReconcileDesired => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.RunReviewCheck => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.RunPipelineSession => TimeSpan.FromMinutes(PipelineMinutes),
        WorkstationCommandKind.CheckClientUpdate => TimeSpan.FromMinutes(Minutes),
        WorkstationCommandKind.UpdateClient => TimeSpan.FromMinutes(Minutes)
    };
}
