namespace ProjectBeacon.Domain.Enums;

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
    ReconcileDesired
}
