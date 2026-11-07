namespace ProjectBeacon.Application.Devices;

using Domain.Enums;
using ProjectBeacon.Application.Common;

/// <summary>Execution context required to handle a workstation command.</summary>
public enum WorkstationCommandScope
{
    Device,
    ProjectRuntime
}

/// <summary>The daemon queue used to execute a workstation command.</summary>
public enum WorkstationCommandLane
{
    Main,
    Control
}

/// <summary>
/// Static metadata for a workstation command. Execution and authorization remain in their
/// dedicated handlers; this catalog centralizes command context, retry, and contract metadata.
/// </summary>
public sealed record WorkstationCommandDescriptor(
    WorkstationCommandKind Kind,
    string OperationId,
    string Summary,
    int Version,
    WorkstationCommandScope Scope,
    OperationRetrySemantics RetrySemantics,
    string PayloadContract,
    string SecurityPolicy,
    WorkstationCommandLane Lane,
    bool StartsControlLane,
    TimeSpan LeaseDuration,
    bool ReconcileBacked,
    bool Enabled = true);

/// <summary>
/// Authoritative metadata catalog for every persisted workstation command kind.
/// Keeping coverage explicit makes newly added enum values fail closed until classified.
/// </summary>
public static class WorkstationCommandCatalog
{
    private static readonly IReadOnlyDictionary<WorkstationCommandKind, WorkstationCommandDescriptor> ByKind = Build();

    public static IReadOnlyCollection<WorkstationCommandDescriptor> All => ByKind.Values.ToArray();

    public static WorkstationCommandDescriptor Get(WorkstationCommandKind kind) =>
        ByKind.TryGetValue(kind, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException($"Workstation command kind '{kind}' has no catalog entry.");

    private static IReadOnlyDictionary<WorkstationCommandKind, WorkstationCommandDescriptor> Build()
    {
        var descriptors = new[]
        {
            Device(WorkstationCommandKind.Probe, "device.probe", "Check workstation connectivity and environment.", OperationRetrySemantics.ReadOnly, "{}"),
            Device(WorkstationCommandKind.ListDir, "workspace.list-directory", "List a directory inside the configured workspace.", OperationRetrySemantics.ReadOnly, "{path:string}"),
            Device(WorkstationCommandKind.Install, "device.install-tool", "Install a supported workstation tool.", OperationRetrySemantics.GuardedMutation, "{tool:string}"),
            Project(WorkstationCommandKind.InitProject, "workspace.initialize", "Initialize project-local development configuration.", OperationRetrySemantics.Idempotent, "{projectId:guid}"),
            Device(WorkstationCommandKind.RetiredAgentConfigApply, "legacy.agent-config.apply", "Retired legacy external-agent configuration command.", OperationRetrySemantics.Reconciliation, "{}", enabled: false),
            Device(WorkstationCommandKind.ScanGguf, "model.scan-local", "Discover local GGUF models.", OperationRetrySemantics.ReadOnly, "{}"),
            Device(WorkstationCommandKind.ReloadProxy, "model-proxy.reload", "Reload the local model proxy configuration.", OperationRetrySemantics.Reconciliation, "{}"),
            Device(WorkstationCommandKind.UnloadProxy, "model-proxy.unload", "Unload a local model from the model proxy.", OperationRetrySemantics.Idempotent, "{}"),
            Device(WorkstationCommandKind.SwapModel, "model.swap", "Switch the active local model.", OperationRetrySemantics.GuardedMutation, "{model:string}"),
            Device(WorkstationCommandKind.SaveWorkstation, "device.save-config", "Persist workstation desired configuration.", OperationRetrySemantics.GuardedMutation, "{revision:integer,...}", reconcileBacked: true),
            Project(WorkstationCommandKind.ChatEnsureSession, "chat.ensure-session", "Ensure a chat runtime session exists.", OperationRetrySemantics.AtMostOnce, "{sessionId?:guid}"),
            Project(WorkstationCommandKind.ChatPrompt, "chat.prompt", "Submit one prompt to a chat session.", OperationRetrySemantics.AtMostOnce, "{sessionId:guid,prompt:string}", startsControlLane: true),
            Device(WorkstationCommandKind.ChatAbort, "chat.abort", "Abort a running chat operation.", OperationRetrySemantics.Idempotent, "{sessionId:guid}", lane: WorkstationCommandLane.Control),
            Device(WorkstationCommandKind.ChatSwitchAgent, "chat.switch-agent", "Switch the active agent for a chat session.", OperationRetrySemantics.AtMostOnce, "{externalSessionId:string,agent:string,chatSessionId?:guid,projectId?:guid}", lane: WorkstationCommandLane.Control),
            Device(WorkstationCommandKind.ChatReplyForm, "chat.reply-form", "Submit an answer to a pending chat form.", OperationRetrySemantics.AtMostOnce, "{externalSessionId:string,formId:string,answerJson:string}", lane: WorkstationCommandLane.Control),
            Device(WorkstationCommandKind.RetiredProviderSync, "legacy.provider.sync", "Retired legacy external-agent provider synchronization command.", OperationRetrySemantics.Reconciliation, "{}", enabled: false),
            Project(WorkstationCommandKind.RunEvalTurn, "eval.run-turn", "Run one evaluation turn.", OperationRetrySemantics.AtMostOnce, "{evalRunId:guid}"),
            Device(WorkstationCommandKind.ReconcileDesired, "device.reconcile", "Reconcile workstation desired state.", OperationRetrySemantics.Reconciliation, "{revision:integer,...}"),
            Project(WorkstationCommandKind.RunReviewCheck, "review.run-check", "Run the trusted project review check.", OperationRetrySemantics.AtMostOnce, "{reviewRunId:guid}"),
            Project(WorkstationCommandKind.RunPipelineSession, "pipeline.run-session", "Run one pipeline agent session on the workstation.", OperationRetrySemantics.AtMostOnce, "{projectId:guid,taskId:guid,subtaskId?:guid,sessionId:guid,role:string,title?:string,prompt:string,model:string,contextLimit?:integer,reasoningEffort?:string}", startsControlLane: true, leaseDuration: TimeSpan.FromMinutes(CommandLease.PipelineMinutes)),
            Device(WorkstationCommandKind.CheckClientUpdate, "device.check-client-update", "Check the latest signed-off workstation client release.", OperationRetrySemantics.ReadOnly, "{}"),
            Device(WorkstationCommandKind.UpdateClient, "device.update-client", "Download, verify, and install the latest workstation client release.", OperationRetrySemantics.AtMostOnce, "{}")
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

    private static WorkstationCommandDescriptor Device(
        WorkstationCommandKind kind,
        string id,
        string summary,
        OperationRetrySemantics retry,
        string payload,
        WorkstationCommandLane lane = WorkstationCommandLane.Main,
        bool startsControlLane = false,
        TimeSpan? leaseDuration = null,
        bool? reconcileBacked = null,
        bool enabled = true) =>
        new(kind, id, summary, 1, WorkstationCommandScope.Device, retry, payload,
            "Device ownership and command policy are enforced by the trusted Application boundary.",
            lane, startsControlLane, leaseDuration ?? TimeSpan.FromMinutes(CommandLease.Minutes),
            reconcileBacked ?? retry == OperationRetrySemantics.Reconciliation, enabled);

    private static WorkstationCommandDescriptor Project(
        WorkstationCommandKind kind,
        string id,
        string summary,
        OperationRetrySemantics retry,
        string payload,
        WorkstationCommandLane lane = WorkstationCommandLane.Main,
        bool startsControlLane = false,
        TimeSpan? leaseDuration = null,
        bool? reconcileBacked = null,
        bool enabled = true) =>
        new(kind, id, summary, 1, WorkstationCommandScope.ProjectRuntime, retry, payload,
            "Project membership, runtime binding, and payload sandboxing are enforced server-side.",
            lane, startsControlLane, leaseDuration ?? TimeSpan.FromMinutes(CommandLease.Minutes),
            reconcileBacked ?? retry == OperationRetrySemantics.Reconciliation, enabled);
}
