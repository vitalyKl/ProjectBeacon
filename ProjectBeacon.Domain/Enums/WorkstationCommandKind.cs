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
    SaveWorkstation,
    ChatEnsureSession,
    ChatPrompt,
    ChatAbort
}
