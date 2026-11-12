namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Diagnostics;
using ProjectBeacon.Application.Runtime;

/// <summary>
/// Owns persistence-backed updates to the user-visible message parts and the terminal state of
/// one native agent turn. Streaming/model/tool orchestration is deliberately outside this service.
/// </summary>
internal sealed class NativeAgentTurnFinalizer(NativeAgentSessionStore sessionStore)
{
    private readonly NativeAgentSessionStore _sessionStore = sessionStore;

    public async Task AppendDeltaAsync(
        NativeAgentSessionState session,
        int turnNumber,
        string messageId,
        string kind,
        int ordinal,
        string delta,
        CancellationToken ct)
    {
        var externalId = $"{messageId}:{kind}:{ordinal}";
        lock (session.Sync)
        {
            var index = session.Parts.FindIndex(part =>
                string.Equals(part.ExternalId, externalId, StringComparison.Ordinal));
            if (index < 0)
                session.Parts.Add(new AgentMessagePart("assistant", kind, delta, externalId, Complete: false));
            else
                session.Parts[index] = session.Parts[index] with
                {
                    Body = session.Parts[index].Body + delta,
                    Complete = false
                };
        }

        await _sessionStore.PersistAsync(session, ct);
        session.Deltas.Writer.TryWrite(new NativeDelta(turnNumber, kind, messageId, ordinal, delta, false));
    }

    public async Task AppendToolPartAsync(
        NativeAgentSessionState session,
        int turnNumber,
        string messageId,
        int ordinal,
        string tool,
        string input,
        string result,
        bool isError,
        int durationMs,
        CancellationToken ct)
    {
        var body = $"{tool}: {result}";
        var externalId = $"{messageId}:tool:{ordinal}";
        var part = new AgentMessagePart(
            "assistant",
            "tool",
            body,
            externalId,
            Complete: true,
            Tool: tool,
            ToolStatus: isError ? "error" : "completed",
            ToolInput: input,
            ToolResult: result,
            DurationMs: durationMs);

        lock (session.Sync)
        {
            var existing = session.Parts.FindIndex(item =>
                string.Equals(item.ExternalId, externalId, StringComparison.Ordinal));
            if (existing < 0)
                session.Parts.Add(part);
            else
                session.Parts[existing] = part;
        }

        await _sessionStore.PersistAsync(session, ct);
        session.Deltas.Writer.TryWrite(new NativeDelta(turnNumber, "tool", messageId, ordinal, body, false));
    }

    public async Task CompleteAssistantPartsAsync(
        NativeAgentSessionState session,
        string messageId,
        CancellationToken ct)
    {
        lock (session.Sync)
        {
            for (var i = 0; i < session.Parts.Count; i++)
            {
                var part = session.Parts[i];
                if (part.Role == "assistant"
                    && part.ExternalId is not null
                    && part.ExternalId.StartsWith(messageId + ":", StringComparison.Ordinal))
                    session.Parts[i] = part with { Complete = true };
            }
        }

        await _sessionStore.PersistAsync(session, ct);
    }

    public async Task AppendErrorAsync(
        NativeAgentSessionState session,
        int turnNumber,
        string messageId,
        string message,
        CancellationToken ct)
    {
        lock (session.Sync)
        {
            var id = $"native-error:{session.Id}:{turnNumber}";
            if (session.Parts.All(part => part.ExternalId != id))
                session.Parts.Add(new AgentMessagePart("assistant", "error", message, id));
            session.TurnStatus = new AgentTurnStatus(false, "error", message, messageId);
        }

        await _sessionStore.PersistAsync(session, ct);
    }

    public async Task FinalizeTurnAsync(
        NativeAgentSessionState session,
        int turnNumber,
        string messageId,
        string finish,
        string? error,
        long firstTokenAt,
        CancellationToken turnToken)
    {
        var elapsed = firstTokenAt == 0 ? 0 : Stopwatch.GetElapsedTime(firstTokenAt).TotalSeconds;
        lock (session.Sync)
        {
            session.Usage = session.Usage with
            {
                AssistantMessages = session.Usage.AssistantMessages + 1,
                GenerationSeconds = session.Usage.GenerationSeconds + elapsed
            };
            session.TurnStatus = new AgentTurnStatus(true, finish, error, messageId);
            if (session.ActiveTurn is { } active && active.Token == turnToken)
            {
                active.Dispose();
                session.ActiveTurn = null;
            }
        }

        try
        {
            await _sessionStore.PersistAsync(session, CancellationToken.None);
        }
        catch
        {
            // The turn must still close its stream even when the final transcript write fails.
        }

        session.Deltas.Writer.TryWrite(NativeDelta.End(turnNumber));
    }
}
