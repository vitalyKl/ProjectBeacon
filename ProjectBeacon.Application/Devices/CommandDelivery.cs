namespace ProjectBeacon.Application.Devices;

using Domain.Enums;
/// <summary>
/// Delivery semantics for workstation commands.
/// A reaper or retry mechanism must consult <see cref="IsAtMostOnce"/> before resetting
/// a Running command to Pending: at-most-once commands must never be re-delivered.
/// <see cref="IsReconcileBacked"/> indicates the command is re-driven by a reconciliation
/// loop, not that it is safe to re-execute on retry.
/// </summary>
public static class CommandDelivery
{
    public static bool IsAtMostOnce(WorkstationCommandKind kind) => kind is
        WorkstationCommandKind.ChatEnsureSession
        or WorkstationCommandKind.ChatPrompt
        or WorkstationCommandKind.RunEvalTurn
        or WorkstationCommandKind.RunReviewCheck;

    public static bool IsIdempotent(WorkstationCommandKind kind) => !IsAtMostOnce(kind);

    public static bool IsReconcileBacked(WorkstationCommandKind kind) => kind is
        WorkstationCommandKind.ApplyOpencode
        or WorkstationCommandKind.ReloadProxy
        or WorkstationCommandKind.SaveWorkstation
        or WorkstationCommandKind.ConfigureOpenCode
        or WorkstationCommandKind.ReconcileDesired;
}
