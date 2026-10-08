namespace ProjectBeacon.Domain.Enums;

/// <summary>
/// Command a workstation client executes. JSON uses these identifier names, not snake_case.
/// Only the enrolled device runs them. The Web host does not.
/// </summary>
public enum WorkstationCommandKind
{
    Probe,
    ListDir,
    Install,
    InitProject,
    ApplyOpencode,
    ScanGguf,
    ReloadProxy,
    UnloadProxy,
    SwapModel,
    SaveWorkstation,
    ChatEnsureSession,
    ChatPrompt,
    ChatAbort,
    ConfigureOpenCode,
    RunEvalTurn,
    ReconcileDesired,
    RunReviewCheck
}
