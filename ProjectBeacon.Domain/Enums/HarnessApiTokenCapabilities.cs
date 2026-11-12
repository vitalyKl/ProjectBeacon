namespace ProjectBeacon.Domain.Enums;

/// <summary>
/// Least-privilege capability profiles for short-lived workstation harness tokens.
/// Chat requests must not inherit pipeline lifecycle permissions.
/// </summary>
public static class HarnessApiTokenCapabilities
{
    public const ApiTokenCapability Chat =
        ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead | ApiTokenCapability.TaskWrite;

    public const ApiTokenCapability Pipeline = Chat | ApiTokenCapability.SessionDrive;

    public const ApiTokenCapability Eval =
        ApiTokenCapability.ContextRead | ApiTokenCapability.TaskRead;

    public static ApiTokenCapability For(WorkstationCommandKind kind) => kind switch
    {
        WorkstationCommandKind.ChatPrompt => Chat,
        WorkstationCommandKind.RunPipelineSession => Pipeline,
        WorkstationCommandKind.RunEvalTurn => Eval,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported harness command kind.")
    };
}
