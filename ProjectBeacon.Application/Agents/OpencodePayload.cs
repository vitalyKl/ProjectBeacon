namespace ProjectBeacon.Application.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Devices;
using Domain.Entities.Agents;
using Domain.Enums;
/// <summary>
/// Builds the OpenCode apply payload, including the beacon-local provider and model ids.
/// </summary>
public static class OpencodePayload
{
    public const string ProviderId = "beacon-local";

    public static string BuildApply(
        AgentRunMode mode,
        IReadOnlyList<LocalModelBackendDto> backends,
        Guid? soloBackendId,
        Guid? plannerId,
        Guid? actorId,
        Guid? reviewId,
        int llamaSwapPort = 8080)
    {
        var byId = backends.ToDictionary(b => b.Id);
        // OpenCode 1.x reads the deprecated "provider" key; 2.x reads the new "providers" key
        // and prefers it when both are present. 2.x rejects a variants array inside the
        // deprecated block (its schema allows a variant map with a disabled flag only), so
        // the variants ride on the new block; v1 resolves reasoning effort per prompt.
        var models = new JsonObject();
        var variantModels = new JsonObject();
        foreach (var backend in backends.Where(b => b.BackendType == ModelBackendType.LlamaCpp).OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            models[ModelKey(backend.Name)] = new JsonObject
            {
                ["name"] = backend.Name,
                ["limit"] = ModelLimit(backend.ContextSize)
            };
            variantModels[ModelKey(backend.Name)] = new JsonObject
            {
                ["name"] = backend.Name,
                ["limit"] = ModelLimit(backend.ContextSize),
                // Each variant sets reasoningEffort, which the OpenAI-compatible client sends as
                // reasoning_effort, accepted per request by llama-server.
                ["variants"] = ReasoningVariants()
            };
        }

        var baseUrl = $"http://127.0.0.1:{llamaSwapPort}/v1";
        var provider = new JsonObject
        {
            [ProviderId] = new JsonObject
            {
                ["npm"] = "@ai-sdk/openai-compatible",
                ["name"] = "Beacon local",
                ["options"] = new JsonObject
                {
                    ["baseURL"] = baseUrl,
                    ["apiKey"] = "beacon"
                },
                ["models"] = models
            }
        };
        var providers = new JsonObject
        {
            [ProviderId] = new JsonObject
            {
                ["name"] = "Beacon local",
                ["package"] = "@opencode/ai/providers/openai-compatible",
                ["settings"] = new JsonObject
                {
                    ["baseURL"] = baseUrl,
                    ["apiKey"] = "beacon"
                },
                ["models"] = variantModels
            }
        };

        string? model = null;
        JsonObject? agent = null;
        if (mode == AgentRunMode.Solo)
        {
            var backend = Resolve(byId, soloBackendId) ?? backends.FirstOrDefault();
            if (backend is not null)
                model = OpenCodeId(backend);
        }
        else
        {
            var planner = Resolve(byId, plannerId);
            var actor = Resolve(byId, actorId);
            var review = Resolve(byId, reviewId);
            if (actor is not null)
                model = OpenCodeId(actor);
            agent = new JsonObject();
            if (actor is not null)
                agent["build"] = new JsonObject { ["model"] = OpenCodeId(actor) };
            if (planner is not null)
                agent["plan"] = new JsonObject { ["model"] = OpenCodeId(planner) };
            if (review is not null)
            {
                agent["review"] = new JsonObject
                {
                    ["mode"] = "subagent",
                    ["description"] = "Cold review of the diff without the originating session.",
                    ["model"] = OpenCodeId(review)
                };
            }
        }

        var root = new JsonObject
        {
            ["path"] = ".",
            ["provider"] = provider,
            ["providers"] = providers
        };
        if (model is not null)
            root["model"] = model;
        if (agent is not null)
            root["agent"] = agent;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    public static string FromBindings(
        IReadOnlyList<RoleBinding> bindings,
        IReadOnlyList<LocalModelBackendDto> backends,
        long revision)
    {
        Guid? Bind(PipelineRole role)
        {
            foreach (var binding in bindings)
            {
                if (binding.Role == role)
                    return binding.ModelBackendId;
            }
            return null;
        }

        var planner = Bind(PipelineRole.Planner);
        var actor = Bind(PipelineRole.Actor);
        var review = Bind(PipelineRole.Review);
        var chosen = new[] { planner, actor, review }.Where(id => id is not null).Select(id => id!.Value).Distinct().Count();
        var mode = chosen > 1 ? AgentRunMode.Pipeline : AgentRunMode.Solo;
        var json = BuildApply(mode, backends, actor ?? planner ?? review, planner, actor, review);
        return DesiredState.WithRevision(json, revision);
    }

    public static string OpenCodeId(LocalModelBackendDto backend) =>
        backend.BackendType == ModelBackendType.LlamaCpp || string.IsNullOrWhiteSpace(backend.OpenCodeModel)
            ? $"{ProviderId}/{ModelKey(backend.Name)}"
            : backend.OpenCodeModel.Trim();

    public static string ModelKey(string name)
    {
        var trimmed = name.Trim();
        return string.IsNullOrEmpty(trimmed) ? "model" : trimmed;
    }

    // limit.context gives OpenCode the server's real context window (without it, 2.x
    // assumes 200 000). limit.output stays at OpenCode's own 32k fallback for custom
    // models, so only the context limit changes; the v1 provider schema requires both.
    public const int DefaultOutputLimit = 32_000;

    private static JsonObject ModelLimit(int contextSize) => new()
    {
        ["context"] = contextSize,
        ["output"] = DefaultOutputLimit
    };

    private static JsonArray ReasoningVariants()
    {
        // A fresh array per call: a JsonNode has a single parent, so the list cannot
        // be shared between model entries.
        return new JsonArray
        {
            new JsonObject { ["id"] = "low", ["settings"] = new JsonObject { ["reasoningEffort"] = "low" } },
            new JsonObject { ["id"] = "medium", ["settings"] = new JsonObject { ["reasoningEffort"] = "medium" } },
            new JsonObject { ["id"] = "high", ["settings"] = new JsonObject { ["reasoningEffort"] = "high" } },
            new JsonObject { ["id"] = "xhigh", ["settings"] = new JsonObject { ["reasoningEffort"] = "xhigh" } }
        };
    }

    private static LocalModelBackendDto? Resolve(IReadOnlyDictionary<Guid, LocalModelBackendDto> byId, Guid? id) =>
        id is { } key && byId.TryGetValue(key, out var backend) ? backend : null;
}