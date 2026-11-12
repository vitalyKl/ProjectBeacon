namespace ProjectBeacon.Cli.Client.AgentHarness;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectBeacon.Application.Runtime;
using ProjectBeacon.Cli.Client;

/// <summary>
/// Owns native session identity, transcript loading/recovery, and durable transcript writes.
/// No model, MCP, command-execution, or turn-orchestration logic belongs here.
/// </summary>
internal sealed class NativeAgentSessionStore(
    Func<WorkstationSettings> loadSettings,
    Func<string, string> buildSystemPrompt)
{
    private readonly Func<WorkstationSettings> _loadSettings = loadSettings;
    private readonly Func<string, string> _buildSystemPrompt = buildSystemPrompt;
    private readonly ConcurrentDictionary<string, NativeAgentSessionState> _sessions = new(StringComparer.Ordinal);

    public IEnumerable<NativeAgentSessionState> Sessions => _sessions.Values;

    public NativeAgentSessionState CreateNew(string title, Guid workstationId = default)
    {
        var settings = _loadSettings();
        var state = new NativeAgentSessionState
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? "Chat" : title.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
            TurnStatus = new AgentTurnStatus(true, "stop", null)
        };
        state.TranscriptPath = TranscriptPath(settings, workstationId, state.Id);
        state.Messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = _buildSystemPrompt(state.AgentMode)
        });
        if (!_sessions.TryAdd(state.Id, state))
            throw new InvalidOperationException("Could not create a unique agent session.");
        return state;
    }

    public async Task<NativeAgentSessionState> GetAsync(string id, CancellationToken ct)
    {
        var normalizedId = NormalizeSessionId(id);
        if (_sessions.TryGetValue(normalizedId, out var existing))
            return existing;

        var path = TranscriptPath(_loadSettings(), Guid.Empty, normalizedId);
        if (!File.Exists(path))
            throw new InvalidOperationException("Agent session was not found.");

        var text = await File.ReadAllTextAsync(path, ct);
        var saved = JsonSerializer.Deserialize<PersistedSession>(text, NativeAgentRuntimeSerialization.JsonOptions)
            ?? throw new InvalidDataException("Stored agent transcript is invalid.");
        if (!string.Equals(saved.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stored agent transcript does not match the requested session.");

        var state = new NativeAgentSessionState
        {
            Id = normalizedId,
            Title = saved.Title,
            CreatedAt = saved.CreatedAt,
            AgentMode = string.Equals(saved.AgentMode, "plan", StringComparison.OrdinalIgnoreCase) ? "plan" : "build",
            TranscriptPath = path,
            Messages = saved.Messages,
            Parts = saved.Parts,
            TurnNumber = saved.TurnNumber,
            LastModel = saved.LastModel,
            ContextLimit = saved.ContextLimit,
            Usage = saved.Usage,
            TurnStatus = saved.TurnStatus,
            ToolJournal = saved.ToolJournal ?? []
        };

        var changed = false;
        lock (state.Sync)
        {
            // Replace obsolete runtime identity prompts when loading older transcripts.
            if (state.Messages.Count == 0
                || state.Messages[0]["role"]?.GetValue<string>() != "system")
            {
                state.Messages.Insert(0, new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = _buildSystemPrompt(state.AgentMode)
                });
                changed = true;
            }
            else if (!string.Equals(
                state.Messages[0]["content"]?.GetValue<string>(),
                _buildSystemPrompt(state.AgentMode),
                StringComparison.Ordinal))
            {
                state.Messages[0]["content"] = _buildSystemPrompt(state.AgentMode);
                changed = true;
            }

            if (state.TurnStatus is { Ended: false } interrupted)
            {
                state.TurnStatus = new AgentTurnStatus(
                    true,
                    "aborted",
                    "Client restarted during the previous turn.",
                    interrupted.MessageId);
                state.Parts = state.Parts.Select(part => part.Complete ? part : part with { Complete = true }).ToList();
                changed = true;
            }

            changed |= RecoverInterruptedToolCalls(state);
        }

        if (!_sessions.TryAdd(normalizedId, state))
            return _sessions[normalizedId];

        if (changed)
        {
            try
            {
                await PersistAsync(state, ct);
            }
            catch
            {
                _sessions.TryRemove(normalizedId, out _);
                throw;
            }
        }

        return state;
    }

    public async Task PersistAsync(NativeAgentSessionState session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        PersistedSession snapshot;
        await session.Persistence.WaitAsync(ct);
        try
        {
            lock (session.Sync)
            {
                snapshot = new PersistedSession
                {
                    Id = session.Id,
                    Title = session.Title,
                    CreatedAt = session.CreatedAt,
                    AgentMode = session.AgentMode,
                    Messages = session.Messages.Select(message => (JsonObject)message.DeepClone()).ToList(),
                    Parts = session.Parts.ToList(),
                    TurnNumber = session.TurnNumber,
                    LastModel = session.LastModel,
                    ContextLimit = session.ContextLimit,
                    Usage = session.Usage,
                    TurnStatus = session.TurnStatus,
                    ToolJournal = session.ToolJournal.ToList()
                };
            }

            var directory = Path.GetDirectoryName(session.TranscriptPath)
                ?? throw new InvalidOperationException("The transcript path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = session.TranscriptPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(snapshot, NativeAgentRuntimeSerialization.JsonOptions),
                    ct);
                File.Move(temporaryPath, session.TranscriptPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(temporaryPath); } catch { }
                throw;
            }
        }
        finally
        {
            session.Persistence.Release();
        }
    }

    internal static string NormalizeSessionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || !Guid.TryParseExact(id, "N", out var parsed)
            || parsed == Guid.Empty)
            throw new InvalidOperationException("Agent session id is invalid.");

        return parsed.ToString("N");
    }

    internal static bool RecoverInterruptedToolCalls(NativeAgentSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var changed = false;
        for (var i = 0; i < session.Messages.Count; i++)
        {
            var assistant = session.Messages[i];
            if (!string.Equals(assistant["role"]?.GetValue<string>(), "assistant", StringComparison.Ordinal)
                || assistant["tool_calls"] is not JsonArray calls
                || calls.Count == 0)
                continue;

            var expected = new List<(string Id, string Name)>();
            foreach (var call in calls)
            {
                var id = call?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var name = call?["function"]?["name"]?.GetValue<string>();
                expected.Add((id, string.IsNullOrWhiteSpace(name) ? "unknown_tool" : name));
            }
            if (expected.Count == 0)
                continue;

            var recorded = new HashSet<string>(StringComparer.Ordinal);
            var insertAt = i + 1;
            while (insertAt < session.Messages.Count
                && string.Equals(session.Messages[insertAt]["role"]?.GetValue<string>(), "tool", StringComparison.Ordinal))
            {
                var resultId = session.Messages[insertAt]["tool_call_id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(resultId))
                    recorded.Add(resultId);
                insertAt++;
            }

            foreach (var call in expected)
            {
                if (!recorded.Add(call.Id))
                    continue;

                const string recoveryMessage =
                    "Tool execution was interrupted before its result was durably recorded. The operation may or may not have completed. " +
                    "It was not replayed automatically to avoid duplicating side effects. Inspect the current state before retrying.";

                session.Messages.Insert(insertAt++, new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = recoveryMessage
                });
                session.Parts.Add(new AgentMessagePart(
                    "assistant",
                    "tool",
                    $"{call.Name}: {recoveryMessage}",
                    $"native-recovery:{session.Id}:{call.Id}",
                    Complete: true,
                    Tool: call.Name,
                    ToolStatus: "error",
                    ToolResult: recoveryMessage));
                changed = true;
            }

            i = insertAt - 1;
        }

        return changed;
    }

    private static string TranscriptPath(WorkstationSettings settings, Guid _, string? id)
    {
        var parent = string.IsNullOrWhiteSpace(settings.AgentDataDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProjectBeacon")
            : Path.GetDirectoryName(Path.GetFullPath(settings.AgentDataDir))
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(parent, "sessions", (id ?? Guid.NewGuid().ToString("N")) + ".json");
    }

    private sealed class PersistedSession
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string AgentMode { get; set; } = "build";
        public List<JsonObject> Messages { get; set; } = [];
        public List<AgentMessagePart> Parts { get; set; } = [];
        public int TurnNumber { get; set; }
        public string? LastModel { get; set; }
        public int? ContextLimit { get; set; }
        public AgentSessionUsage Usage { get; set; } = new(0, 0, 0, 0);
        public AgentTurnStatus? TurnStatus { get; set; }
        public List<NativeToolJournalEntry>? ToolJournal { get; set; }
    }
}
