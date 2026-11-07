namespace ProjectBeacon.Application.Devices;

using Domain.Enums;
using ProjectBeacon.Application.Common;

/// <summary>
/// Delivery and reconciliation semantics for workstation commands. The command catalog is the
/// single source of truth so recovery cannot silently drift from the declared operation policy.
/// </summary>
public static class CommandDelivery
{
    public static bool IsAtMostOnce(WorkstationCommandKind kind) =>
        WorkstationCommandCatalog.Get(kind).RetrySemantics == OperationRetrySemantics.AtMostOnce;

    public static bool IsIdempotent(WorkstationCommandKind kind) =>
        !IsAtMostOnce(kind);

    public static bool IsReconcileBacked(WorkstationCommandKind kind) =>
        WorkstationCommandCatalog.Get(kind).ReconcileBacked;
}
