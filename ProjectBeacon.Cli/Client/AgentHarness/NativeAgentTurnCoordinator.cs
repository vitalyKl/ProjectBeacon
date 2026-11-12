namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client.ModelSwapping;

/// <summary>
/// Orchestrates one full agent turn by coordinating provider, context, tool catalog,
/// model-step, tool-execution, transcript, and turn-finalization services.
/// It owns turn sequencing, not the implementation of those individual responsibilities.
/// </summary>
internal sealed class NativeAgentTurnCoordinator(
    NativeAgentSessionStore sessionStore,
    NativeAgentProviderService providerService,
    NativeAgentToolCatalog toolCatalog,
    NativeAgentModelStepRunner modelStepRunner,
    NativeAgentTurnFinalizer turnFinalizer,
    NativeAgentToolCoordinator toolCoordinator,
    Func<AgentHarnessAuthorization, CancellationToken, Task<IMcpToolClient>> mcpClientFactory,
    Action<string>? log)
{
    private readonly NativeAgentSessionStore _sessionStore = sessionStore;
    private readonly NativeAgentProviderService _providerService = providerService;
    private readonly NativeAgentToolCatalog _toolCatalog = toolCatalog;
    private readonly NativeAgentModelStepRunner _modelStepRunner = modelStepRunner;
    private readonly NativeAgentTurnFinalizer _turnFinalizer = turnFinalizer;
    private readonly NativeAgentToolCoordinator _toolCoordinator = toolCoordinator;
    private readonly Func<AgentHarnessAuthorization, CancellationToken, Task<IMcpToolClient>> _mcpClientFactory = mcpClientFactory;
    private readonly Action<string>? _log = log;

    public async Task RunAsync(
        NativeAgentSessionState session,
        int turnNumber,
        string model,
        AgentPromptControls? controls,
        IModelTaskLease taskLease,
        CancellationToken ct)
    {
        var messageId = $"native:{session.Id}:{turnNumber}";
        string agentMode;
        lock (session.Sync)
            agentMode = session.AgentMode;
        var firstTokenAt = 0L;
        var finish = "stop";
        string? error = null;
        var emittedContent = false;
        try
        {
            await using var providerSession = await _providerService.OpenSessionAsync(model, ct);
            await using var mcpClient = controls?.HarnessAuthorization is { } harnessAuthorization
                ? await _mcpClientFactory(harnessAuthorization, ct)
                : null;
            var toolCatalog = _toolCatalog.Build(agentMode, controls, mcpClient);
            var mcpTools = toolCatalog.Tools;

            var hasFinalText = false;
            var loopState = new NativeAgentToolLoopState();
            var stepOrdinal = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // Each provider request is a distinct assistant response step. Do not merge
                // reasoning or text emitted after a tool result into the preceding step.
                var step = await _modelStepRunner.ExecuteAsync(
                    session,
                    turnNumber,
                    stepOrdinal,
                    messageId,
                    providerSession.Model,
                    mcpTools,
                    controls,
                    providerSession.SummarizeAsync,
                    providerSession.CreateProvider,
                    providerSession.RunModelRequestAsync,
                    firstTokenAt,
                    emittedContent,
                    ct);
                firstTokenAt = step.FirstTokenAt;
                emittedContent = step.EmittedContent;

                var toolBatch = await _toolCoordinator.ExecuteBatchAsync(
                    session,
                    turnNumber,
                    messageId,
                    step,
                    agentMode,
                    controls,
                    mcpClient,
                    toolCatalog,
                    taskLease,
                    loopState,
                    ct);
                if (toolBatch.Error is not null)
                {
                    finish = "error";
                    error = toolBatch.Error;
                    break;
                }

                if (toolBatch.IsTerminal)
                {
                    finish = "stop";
                    hasFinalText = true;
                    break;
                }

                if (step.ToolCalls.Count > 0)
                {
                    finish = "tool_calls";
                    stepOrdinal++;
                    continue;
                }

                if (step.Text.Length > 0)
                {
                    lock (session.Sync)
                        session.Messages.Add(new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = step.Text
                        });
                    await _sessionStore.PersistAsync(session, ct);
                    hasFinalText = true;
                }

                finish = step.FinishReason;
                break;
            }

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            if (error is not null)
            {
                await _turnFinalizer.CompleteAssistantPartsAsync(session, messageId, ct);
                await _turnFinalizer.AppendErrorAsync(session, turnNumber, messageId, error, ct);
            }
            else if (!hasFinalText)
            {
                error = emittedContent
                    ? "The model completed the turn without a final text answer."
                    : "The model returned no text.";
                finish = "error";
                await _turnFinalizer.CompleteAssistantPartsAsync(session, messageId, ct);
                await _turnFinalizer.AppendErrorAsync(session, turnNumber, messageId, error, ct);
            }
            else
            {
                await _turnFinalizer.CompleteAssistantPartsAsync(session, messageId, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            finish = "aborted";
            error = null;
            await _turnFinalizer.CompleteAssistantPartsAsync(session, messageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            finish = "error";
            error = SafeError(ex);
            try { await _turnFinalizer.AppendErrorAsync(session, turnNumber, messageId, error, CancellationToken.None); } catch { }
            _log?.Invoke($"native agent turn failed: {error}");
        }
        finally
        {
            await _turnFinalizer.FinalizeTurnAsync(
                session, turnNumber, messageId, finish, error, firstTokenAt, ct);
        }
    }


    private static string SafeError(Exception ex) => ex switch
    {
        ChatProviderException provider => provider.Message,
        HttpRequestException => "The chat provider connection failed.",
        OperationCanceledException => "The generation was cancelled.",
        InvalidDataException => ex.Message,
        InvalidOperationException => ex.Message,
        _ => "The native agent runtime encountered an unexpected error."
    };
}
