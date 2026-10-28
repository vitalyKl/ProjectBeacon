namespace ProjectBeacon.Application.Devices;

using Domain.Enums;

/// <summary>Execution context required to handle a workstation command.</summary>
public enum WorkstationCommandScope
{
    Device,
    ProjectRuntime
}

/// <summary>
/// Static metadata for a workstation command. Execution and authorization remain in their
/// dedicated handlers; this catalog centralizes command context requirements.
/// </summary>
public sealed record WorkstationCommandDescriptor(
    WorkstationCommandKind Kind,
    WorkstationCommandScope Scope);

/// <summary>
/// Authoritative metadata catalog for every persisted workstation command kind.
/// Keeping coverage explicit makes newly added enum values fail closed until classified.
/// </summary>
public static class WorkstationCommandCatalog
{
    private static readonly IReadOnlyDictionary<WorkstationCommandKind, WorkstationCommandDescriptor> ByKind =
        Build();

    public static IReadOnlyCollection<WorkstationCommandDescriptor> All => ByKind.Values;

    public static WorkstationCommandDescriptor Get(WorkstationCommandKind kind) =>
        ByKind.TryGetValue(kind, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException($"Workstation command kind '{kind}' has no catalog entry.");

    private static IReadOnlyDictionary<WorkstationCommandKind, WorkstationCommandDescriptor> Build()
    {
        var descriptors = new[]
        {
            Device(WorkstationCommandKind.Probe),
            Device(WorkstationCommandKind.ListDir),
            Device(WorkstationCommandKind.Install),
            Project(WorkstationCommandKind.InitProject),
            Project(WorkstationCommandKind.ApplyOpencode),
            Device(WorkstationCommandKind.ScanGguf),
            Device(WorkstationCommandKind.ReloadProxy),
            Device(WorkstationCommandKind.UnloadProxy),
            Device(WorkstationCommandKind.SwapModel),
            Device(WorkstationCommandKind.SaveWorkstation),
            Project(WorkstationCommandKind.ChatEnsureSession),
            Project(WorkstationCommandKind.ChatPrompt),
            Device(WorkstationCommandKind.ChatAbort),
            Device(WorkstationCommandKind.ConfigureOpenCode),
            Project(WorkstationCommandKind.RunEvalTurn),
            Device(WorkstationCommandKind.ReconcileDesired),
            Project(WorkstationCommandKind.RunReviewCheck)
        };

        var byKind = descriptors.ToDictionary(descriptor => descriptor.Kind);
        var missing = Enum.GetValues<WorkstationCommandKind>().Where(kind => !byKind.ContainsKey(kind)).ToArray();
        var extra = byKind.Keys.Where(kind => !Enum.IsDefined(kind)).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
        {
            var details = new List<string>();
            if (missing.Length > 0)
                details.Add($"missing: {string.Join(", ", missing)}");
            if (extra.Length > 0)
                details.Add($"undefined: {string.Join(", ", extra)}");
            throw new InvalidOperationException($"Workstation command catalog is inconsistent ({string.Join("; ", details)}).");
        }

        return byKind;
    }

    private static WorkstationCommandDescriptor Device(WorkstationCommandKind kind) =>
        new(kind, WorkstationCommandScope.Device);

    private static WorkstationCommandDescriptor Project(WorkstationCommandKind kind) =>
        new(kind, WorkstationCommandScope.ProjectRuntime);
}
