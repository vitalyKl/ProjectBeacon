namespace ProjectBeacon.Application.Agents;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Enums;

public static class OpencodePayload
{
    public const string ProviderId = "beacon-local";

    public static string BuildApply(
        string localRoot,
        AgentRunMode mode,
        IReadOnlyList<LocalModelBackendDto> backends,
        Guid? soloBackendId,
        Guid? plannerId,
        Guid? actorId,
        Guid? reviewId,
        int llamaSwapPort = 8080)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        var byId = backends.ToDictionary(b => b.Id);
        var models = new JsonObject();
        foreach (var backend in backends.Where(b => b.BackendType == ModelBackendType.LlamaCpp).OrderBy(b => b.Name, StringComparer.Ordinal))
            models[ModelKey(backend.Name)] = new JsonObject { ["name"] = backend.Name };

        var provider = new JsonObject
        {
            [ProviderId] = new JsonObject
            {
                ["npm"] = "@ai-sdk/openai-compatible",
                ["name"] = "Beacon local",
                ["options"] = new JsonObject
                {
                    ["baseURL"] = $"http://127.0.0.1:{llamaSwapPort}/v1",
                    ["apiKey"] = "beacon"
                },
                ["models"] = models
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
            ["path"] = localRoot,
            ["provider"] = provider
        };
        if (model is not null)
            root["model"] = model;
        if (agent is not null)
            root["agent"] = agent;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
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

    private static LocalModelBackendDto? Resolve(IReadOnlyDictionary<Guid, LocalModelBackendDto> byId, Guid? id) =>
        id is { } key && byId.TryGetValue(key, out var backend) ? backend : null;
}
