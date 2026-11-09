namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Common;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> GovernTools() =>
    [
        new(McpToolNames.ListConstraints, "List project constraints.",
            () => Props(("projectId", "string", false)), ConstraintsList),
        new(McpToolNames.ConstraintPropose, "Propose a new constraint. It remains Proposed until a human activates it.",
            () => Props(("body", "string", true), ("kind", "string", true), ("projectId", "string", false)), ConstraintPropose),
        new(McpToolNames.CreateConstraint, "Create a constraint (human operation). kind: Must, MustNot, Security, Compliance.",
            () => Props(("body", "string", true), ("kind", "string", true), ("projectId", "string", false)), ConstraintCreate),
        new(McpToolNames.ActivateConstraint, "Activate a proposed constraint.",
            () => Props(("constraintId", "string", true), ("projectId", "string", false)), (args, api, env, ct) => ConstraintPost(args, "activate", api, env, ct)),
        new(McpToolNames.RejectConstraint, "Reject a proposed constraint.",
            () => Props(("constraintId", "string", true), ("projectId", "string", false)), (args, api, env, ct) => ConstraintPost(args, "reject", api, env, ct)),
        new(McpToolNames.ListDecisions, "List project decisions.",
            () => Props(("projectId", "string", false)), DecisionsList),
        new(McpToolNames.DecisionPropose, "Propose a decision. It remains Proposed until a human accepts it.",
            () => Props(("title", "string", true), ("body", "string", true), ("context", "string", false),
                ("consequences", "string", false), ("projectId", "string", false)), DecisionPropose),
        new(McpToolNames.RecordDecision, "Record a decision (human operation).",
            () => Props(("title", "string", true), ("body", "string", true), ("context", "string", false),
                ("consequences", "string", false), ("projectId", "string", false)), DecisionCreate),
        new(McpToolNames.AcceptDecision, "Accept a proposed decision.",
            () => Props(("decisionId", "string", true), ("projectId", "string", false)), (args, api, env, ct) => DecisionPost(args, "accept", api, env, ct)),
        new(McpToolNames.DeprecateDecision, "Deprecate a decision.",
            () => Props(("decisionId", "string", true), ("projectId", "string", false)), (args, api, env, ct) => DecisionPost(args, "deprecate", api, env, ct)),
        new(McpToolNames.SupersedeDecision, "Supersede an accepted decision with another decision.",
            () => Props(("decisionId", "string", true), ("replacementId", "string", true), ("projectId", "string", false)), DecisionSupersede),
    ];

    private static async Task<McpToolText> ConstraintsList(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/constraints", null, ct);
    }

    private static async Task<McpToolText> ConstraintPropose(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var bodyText = Text(args, "body");
        var kind = Text(args, "kind");
        if (string.IsNullOrWhiteSpace(bodyText) || string.IsNullOrWhiteSpace(kind))
            return new McpToolText(true, "body and kind are required");
        var body = new JsonObject { ["body"] = bodyText, ["kind"] = kind };
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/constraints/propose", body, ct);
    }

    private static async Task<McpToolText> ConstraintCreate(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var bodyText = Text(args, "body");
        var kind = Text(args, "kind");
        if (string.IsNullOrWhiteSpace(bodyText) || string.IsNullOrWhiteSpace(kind))
            return new McpToolText(true, "body and kind are required");
        var body = new JsonObject { ["body"] = bodyText, ["kind"] = kind };
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/constraints", body, ct);
    }

    private static async Task<McpToolText> ConstraintPost(JsonObject? args, string action, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "constraintId", out var constraintId))
            return new McpToolText(true, "missing or invalid 'constraintId'");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/constraints/{constraintId:D}/{action}", new JsonObject(), ct);
    }

    private static async Task<McpToolText> DecisionsList(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        return await SendAsync(api, HttpMethod.Get, $"v1/projects/{projectId:D}/decisions", null, ct);
    }

    private static async Task<McpToolText> DecisionPropose(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var title = Text(args, "title");
        var decisionBody = Text(args, "body");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(decisionBody))
            return new McpToolText(true, "title and body are required");
        var body = new JsonObject { ["title"] = title, ["body"] = decisionBody };
        Put(body, "context", Text(args, "context"));
        Put(body, "consequences", Text(args, "consequences"));
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/decisions/propose", body, ct);
    }

    private static async Task<McpToolText> DecisionCreate(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        var title = Text(args, "title");
        var decisionBody = Text(args, "body");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(decisionBody))
            return new McpToolText(true, "title and body are required");
        var body = new JsonObject { ["title"] = title, ["body"] = decisionBody };
        Put(body, "context", Text(args, "context"));
        Put(body, "consequences", Text(args, "consequences"));
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/decisions", body, ct);
    }

    private static async Task<McpToolText> DecisionPost(JsonObject? args, string action, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "decisionId", out var decisionId))
            return new McpToolText(true, "missing or invalid 'decisionId'");
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/decisions/{decisionId:D}/{action}", new JsonObject(), ct);
    }

    private static async Task<McpToolText> DecisionSupersede(JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var error))
            return new McpToolText(true, error);
        if (!TryGuid(args, "decisionId", out var decisionId))
            return new McpToolText(true, "missing or invalid 'decisionId'");
        if (!TryGuid(args, "replacementId", out var replacementId))
            return new McpToolText(true, "missing or invalid 'replacementId'");
        var body = new JsonObject { ["replacementId"] = replacementId.ToString("D") };
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/decisions/{decisionId:D}/supersede", body, ct);
    }

}