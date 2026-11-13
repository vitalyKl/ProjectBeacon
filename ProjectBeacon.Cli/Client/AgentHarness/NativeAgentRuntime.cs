namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;
using ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Workstation-local IAgentRuntime adapter. It composes focused session and turn services and owns
/// their workstation lifecycle; provider, context, tool, and transcript behavior live in dedicated
/// components rather than the Web, API, or Worker processes.
/// </summary>
internal sealed class NativeAgentRuntime : IAgentRuntime, IAgentRuntimeCapabilities
{
    private readonly ClientLlamaSwap _llama;
    private readonly IDaemonLlamaSync _llamaSync;
    private readonly Func<WorkstationSettings> _loadSettings;
    private readonly Action<string>? _log;
    private readonly NativeServiceManager _services = new();
    private readonly NativeAgentSessionStore _sessionStore;
    private readonly NativeAgentTurnCoordinator _turnCoordinator;
    private volatile bool _disposed;

    public NativeAgentRuntime(
        HttpClient controlPlane,
        ClientLlamaSwap llama,
        IDaemonLlamaSync llamaSync,
        Func<WorkstationSettings> loadSettings,
        Action<string>? log = null,
        Func<HttpClient, string, string?, IChatProvider>? providerFactory = null,
        Func<AgentHarnessAuthorization, CancellationToken, Task<IMcpToolClient>>? mcpClientFactory = null,
        Action<WorkstationSettings>? saveSettings = null)
    {
        _llama = llama;
        _llamaSync = llamaSync;
        _loadSettings = loadSettings;
        _log = log;
        _sessionStore = new NativeAgentSessionStore(_loadSettings, NativeAgentPromptCatalog.BuildSystemPrompt);

        // Compose focused services once. Runtime remains the IAgentRuntime adapter and owns
        // only session-facing operations and the outer workstation lifecycle.
        var commandApproval = new NativeAgentCommandApprovalService(_log);
        var turnFinalizer = new NativeAgentTurnFinalizer(_sessionStore);
        var contextBuilder = new NativeAgentContextBuilder(_loadSettings, _log, _sessionStore.PersistAsync);
        var modelStepRunner = new NativeAgentModelStepRunner(
            contextBuilder,
            turnFinalizer.AppendDeltaAsync,
            _log);
        var toolExecutor = new NativeAgentToolExecutor(
            _loadSettings,
            _services,
            commandApproval,
            _log);
        var toolCoordinator = new NativeAgentToolCoordinator(
            _sessionStore,
            toolExecutor,
            turnFinalizer);

        var providerService = new NativeAgentProviderService(
            controlPlane,
            _llama,
            _llamaSync,
            _loadSettings,
            providerFactory ?? ((http, url, key) => new OpenAiCompatProvider(http, url, key)));
        var toolCatalog = new NativeAgentToolCatalog(_loadSettings);
        var scopedMcpClientFactory = mcpClientFactory ?? StartMcpClientAsync;
        _turnCoordinator = new NativeAgentTurnCoordinator(
            _sessionStore,
            providerService,
            toolCatalog,
            modelStepRunner,
            turnFinalizer,
            toolCoordinator,
            scopedMcpClientFactory,
            _log);
    }

    private Task<IMcpToolClient> StartMcpClientAsync(
        AgentHarnessAuthorization authorization,
        CancellationToken ct)
        => ProjectMcpToolClient.StartAsync(authorization, _log, ct);

    public bool SupportsForms => true;
    public bool SupportsAgentSwitch => true;
    public bool SupportsReasoningEffort => true;
    public bool SupportsLiveDeltas => true;
    public bool SupportsConcurrentSessions => true;

    public async Task<string> CreateSessionAsync(string title, CancellationToken ct)
    {
        ThrowIfDisposed();
        var state = _sessionStore.CreateNew(title);
        await _sessionStore.PersistAsync(state, ct);
        return state.Id;
    }

    public async Task SendPromptAsync(
        string sessionId,
        string text,
        string? model,
        CancellationToken ct,
        AgentPromptControls? controls = null)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Prompt text is required.", nameof(text));
        var session = await _sessionStore.GetAsync(sessionId, ct);
        var selectedModel = string.IsNullOrWhiteSpace(model) ? session.LastModel : model.Trim();
        if (string.IsNullOrWhiteSpace(selectedModel))
            throw new InvalidOperationException("Select a model before sending a prompt.");

        CancellationTokenSource turnCts;
        int turnNumber;
        int previousTurnNumber;
        AgentTurnStatus? previousTurnStatus;
        string? previousModel;
        int? previousContextLimit;
        JsonObject userMessage;
        lock (session.Sync)
        {
            if (session.ActiveTurn is not null)
                throw new InvalidOperationException("A prompt is already running for this session.");

            // The lock first confirms that no turn is active, then repairs any abandoned tool
            // calls and reserves the session. A concurrent prompt must never "recover" a live turn.
            NativeAgentSessionStore.RecoverInterruptedToolCalls(session);

            previousTurnNumber = session.TurnNumber;
            previousTurnStatus = session.TurnStatus;
            previousModel = session.LastModel;
            previousContextLimit = session.ContextLimit;

            turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            session.ActiveTurn = turnCts;
            if (!string.Equals(previousModel, selectedModel, StringComparison.Ordinal))
            {
                // Another model counts tokens differently; its last reported prompt size is not comparable.
                session.LastPromptTokens = 0;
                session.LastPromptMessageCount = 0;
            }
            session.LastModel = selectedModel;
            session.ContextLimit = controls?.ContextLimit is > 0 ? controls.ContextLimit : session.ContextLimit;
            turnNumber = ++session.TurnNumber;
            session.TurnStatus = new AgentTurnStatus(false, null, null, session.TurnStatus?.MessageId);
            if (!string.IsNullOrWhiteSpace(controls?.SystemPromptAddition))
            {
                var systemMessage = session.Messages.FirstOrDefault(message =>
                    string.Equals(message["role"]?.GetValue<string>(), "system", StringComparison.OrdinalIgnoreCase));
                if (systemMessage is not null)
                {
                    var currentSystemPrompt = systemMessage["content"]?.GetValue<string>() ?? string.Empty;
                    if (!currentSystemPrompt.Contains(controls.SystemPromptAddition, StringComparison.Ordinal))
                        systemMessage["content"] = currentSystemPrompt + Environment.NewLine + Environment.NewLine
                            + controls.SystemPromptAddition.Trim();
                }
            }
            userMessage = new JsonObject { ["role"] = "user", ["content"] = text };
            session.Messages.Add(userMessage);
        }

        try
        {
            await _sessionStore.PersistAsync(session, ct);
        }
        catch
        {
            // Do not leave the session reserved when the user prompt could not be durably stored.
            lock (session.Sync)
            {
                if (ReferenceEquals(session.ActiveTurn, turnCts))
                {
                    session.ActiveTurn = null;
                    session.TurnNumber = previousTurnNumber;
                    session.TurnStatus = previousTurnStatus;
                    session.LastModel = previousModel;
                    session.ContextLimit = previousContextLimit;
                    session.Messages.Remove(userMessage);
                }
            }
            turnCts.Dispose();
            throw;
        }

        var isLocalModel = selectedModel.StartsWith("beacon-local/", StringComparison.OrdinalIgnoreCase);
        var localModelName = isLocalModel ? selectedModel["beacon-local/".Length..] : string.Empty;
        var concurrentTask = !isLocalModel || _llama.IsModelConcurrent(localModelName);
        var task = _llamaSync.RunModelTaskAsync(
            concurrentTask,
            taskLease => RunTurnAsync(session, turnNumber, selectedModel, controls, taskLease, turnCts.Token),
            turnCts.Token);
        lock (session.Sync)
            session.ActiveTask = task;
    }

    public async IAsyncEnumerable<AgentMessagePart> StreamPartsAsync(
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct);
        AgentMessagePart[] snapshot;
        lock (session.Sync)
            snapshot = session.Parts.ToArray();
        foreach (var part in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            yield return part;
        }
    }

    public async Task<AgentTurnStatus?> GetTurnStatusAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct);
        lock (session.Sync)
            return session.TurnStatus;
    }

    public async Task AbortAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct);
        CancellationTokenSource? active;
        Task? activeTask;
        lock (session.Sync)
        {
            active = session.ActiveTurn;
            activeTask = session.ActiveTask;
        }

        active?.Cancel();
        if (activeTask is not null)
            await activeTask.WaitAsync(ct);
        await _services.StopAllAsync(sessionId, ct);
    }

    public Task StopServicesAsync(string sessionId, CancellationToken ct = default)
        => _services.StopAllAsync(sessionId, ct);

    public async Task<AgentSessionUsage> ReadUsageAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct);
        lock (session.Sync)
            return session.Usage;
    }

    public Task<AgentRuntimeReadiness> EnsureReadyAsync(string? workingDirectory, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Task.FromResult(new AgentRuntimeReadiness(false, "A project working directory is required.", null));
        var fullPath = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(fullPath))
            return Task.FromResult(new AgentRuntimeReadiness(false, "The selected project directory does not exist.", fullPath));
        // Provider reachability is intentionally checked on the first request: this readiness
        // probe must not make a live network call or reject cloud providers because the local
        // llama-server is currently unloaded.
        var warning = _llama.NativeToolCallingWarning();
        if (!string.IsNullOrWhiteSpace(warning))
            _log?.Invoke(warning);
        return Task.FromResult(new AgentRuntimeReadiness(true, null, fullPath, warning));
    }

    public async Task StreamTextAsync(
        string sessionId,
        Func<string, string, int, string, CancellationToken, Task> onDelta,
        CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct);
        int targetTurn;
        lock (session.Sync)
            targetTurn = session.TurnNumber + 1;

        await foreach (var item in session.Deltas.Reader.ReadAllAsync(ct))
        {
            if (item.TurnNumber < targetTurn)
                continue;
            if (item.TurnNumber > targetTurn)
                return;
            if (item.IsTurnEnd)
                return;
            await onDelta(item.Kind, item.MessageId, item.Ordinal, item.Text, ct);
        }
    }

    public async Task<IReadOnlyList<AgentRuntimeForm>> ListPendingFormsAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct);
        return session.PendingForms.Values
            .Select(pending => pending.Form)
            .OrderBy(form => form.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<bool> ReplyFormAsync(string sessionId, string formId, string answerJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(answerJson))
            return false;
        try
        {
            using var document = JsonDocument.Parse(answerJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
        }
        catch (JsonException)
        {
            return false;
        }
        var session = await _sessionStore.GetAsync(sessionId, ct);
        return session.PendingForms.TryGetValue(formId, out var pending)
            && pending.TrySetResult(answerJson);
    }

    public async Task SwitchAgentAsync(string sessionId, string agent, CancellationToken ct)
    {
        var mode = agent?.Trim().ToLowerInvariant();
        if (mode is not ("build" or "plan"))
            throw new ArgumentException("Agent mode must be 'build' or 'plan'.", nameof(agent));

        var session = await _sessionStore.GetAsync(sessionId, ct);
        lock (session.Sync)
        {
            if (session.ActiveTurn is not null)
                throw new InvalidOperationException("The agent mode cannot be changed while a response is running.");

            session.AgentMode = mode;
            if (session.Messages.Count == 0)
                session.Messages.Add(new JsonObject { ["role"] = "system" });
            session.Messages[0]["role"] = "system";
            session.Messages[0]["content"] = NativeAgentPromptCatalog.BuildSystemPrompt(mode);
        }

        await _sessionStore.PersistAsync(session, ct);
    }

    public Task<int?> ReadContextLimitAsync(string? modelId, CancellationToken ct)
    {
        var limit = _sessionStore.Sessions
            .Where(s => string.IsNullOrWhiteSpace(modelId) || string.Equals(s.LastModel, modelId, StringComparison.Ordinal))
            .Select(s => s.ContextLimit)
            .FirstOrDefault(value => value is > 0);
        return Task.FromResult(limit);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var states = _sessionStore.Sessions.ToArray();
        var tasks = new List<Task>();
        foreach (var session in states)
        {
            lock (session.Sync)
            {
                session.ActiveTurn?.Cancel();
                if (session.ActiveTask is not null)
                    tasks.Add(session.ActiveTask);
            }
        }
        try { await Task.WhenAll(tasks); } catch { }
        foreach (var session in states)
        {
            try { await _services.StopAllAsync(session.Id, CancellationToken.None); } catch { }
            NativeCommandRunner.CleanupSession(session.Id);
            lock (session.Sync)
            {
                session.ActiveTurn?.Dispose();
                session.ActiveTurn = null;
            }
            session.Deltas.Writer.TryComplete();
        }
        await _services.DisposeAsync();
    }

    private Task RunTurnAsync(
        NativeAgentSessionState session,
        int turnNumber,
        string model,
        AgentPromptControls? controls,
        IModelTaskLease taskLease,
        CancellationToken ct)
        => _turnCoordinator.RunAsync(session, turnNumber, model, controls, taskLease, ct);

    internal static int EstimatePromptTokens(
        IReadOnlyList<JsonObject> messages, int toolTokens, int lastPromptTokens, int lastPromptMessageCount)
        => NativeAgentContextBuilder.EstimatePromptTokens(messages, toolTokens, lastPromptTokens, lastPromptMessageCount);

    internal static int CalculatePromptBudget(int contextLimit, int configuredReserveTokens)
        => NativeAgentContextBuilder.CalculatePromptBudget(contextLimit, configuredReserveTokens);

    internal static int EstimateToolTokens(JsonArray tools)
        => NativeAgentContextBuilder.EstimateToolTokens(tools);

    internal static List<JsonObject> PrepareSummaryInput(IReadOnlyList<JsonObject> old, int limit)
        => NativeAgentContextBuilder.PrepareSummaryInput(old, limit);

    internal static int ElideOlderToolResults(IList<JsonObject> messages, int threshold)
        => NativeAgentContextBuilder.ElideOlderToolResults(messages, threshold);

    internal static int FindSafeContextSplitIndex(IReadOnlyList<JsonObject> messages, int requestedIndex)
        => NativeAgentContextBuilder.FindSafeContextSplitIndex(messages, requestedIndex);

    internal static AgentRuntimeForm CreateCommandPermissionForm(
        string formId, string workingDirectory, IReadOnlyList<string> argv, string prefix)
        => NativeAgentCommandApprovalService.CreateCommandPermissionForm(formId, workingDirectory, argv, prefix);

    internal static string ParseCommandPermissionDecision(string? answerJson)
        => NativeAgentCommandApprovalService.ParseCommandPermissionDecision(answerJson);

    internal static bool IsPlanToolAllowed(string name)
        => NativeAgentToolPolicy.IsPlanToolAllowed(name);

    internal static bool IsPlanModeCommandExecutionBlocked(string agentMode, string? toolName)
        => NativeAgentToolPolicy.IsPlanModeCommandExecutionBlocked(agentMode, toolName);

    internal static JsonArray FilterPlanTools(JsonArray tools)
        => NativeAgentToolPolicy.FilterPlanTools(tools);

    internal static JsonArray FilterDeniedTools(JsonArray tools, IReadOnlyList<string> deniedTools)
        => NativeAgentToolPolicy.FilterDeniedTools(tools, deniedTools);

    internal static bool IsSuccessfulTerminalTool(string? role, string? toolName, string? argumentsJson)
        => NativeAgentToolPolicy.IsSuccessfulTerminalTool(role, toolName, argumentsJson);

    internal static bool IsAdvertisedMcpTool(JsonArray tools, string name)
        => NativeAgentToolPolicy.IsAdvertisedMcpTool(tools, name);

    internal static bool IsCacheableTool(string? name)
        => NativeAgentToolPolicy.IsCacheableTool(name);

    internal static bool IsInteractiveChatAuthorization(AgentHarnessAuthorization authorization)
        => NativeAgentToolPolicy.IsInteractiveChatAuthorization(authorization);

    internal static bool IsRunCommandAuthorized(AgentHarnessAuthorization authorization)
        => NativeAgentToolPolicy.IsRunCommandAuthorized(authorization);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NativeAgentRuntime));
    }



}
