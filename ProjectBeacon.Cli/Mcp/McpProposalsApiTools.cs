namespace ProjectBeacon.Cli.Mcp;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Common;
using Domain.Enums;

internal static partial class McpApiTools
{
    private static IEnumerable<McpTool> ProposalTools() =>
    [
        new(McpToolNames.KnowledgePropose,
            "Submit a revision-bound content proposal for human review. Use Update for edits to existing content; Create is supported only for a new Milestone (omit targetId, use baseRevision 0); Delete is supported for reviewed ContextSection/Milestone and accepted Decision/active Constraint targets (use the exact current baseRevision and payload {}). Existing Proposed Decision/Constraint and NeedsReview ContextSection creation remain on their existing tools. RepoPatch is Update-only and requires a valid unified diff plus a files manifest with gitPath, projectPath, baseHash and resultHash; use hash_file/hash_text for exact SHA-256 digests. The proposal is scoped to the current Actor subtask and never applies content itself.",
            () => Props(
                ("targetKind", "string", true),
                ("targetId", "string", false),
                ("operation", "string", false),
                ("baseRevision", "integer", true),
                ("payload", "object", true),
                ("rationaleMarkdown", "string", true),
                ("projectId", "string", false)),
            KnowledgePropose),
        new(McpToolNames.KnowledgeProposals,
            "List proposals authored by this Actor subtask, including status and human rejection notes. Does not expose other actors' or users' proposals.",
            () => Props(
                ("status", "string", false),
                ("projectId", "string", false)),
            KnowledgeProposals),
        new(McpToolNames.KnowledgeWithdraw,
            "Withdraw a pending proposal authored by this Actor subtask.",
            () => Props(
                ("proposalId", "string", true),
                ("projectId", "string", false)),
            KnowledgeWithdraw)
    ];

    private static async Task<McpToolText> KnowledgePropose(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var projectError))
            return new McpToolText(true, projectError);

        if (!Guid.TryParse(env.Get("BEACON_SUBTASK_ID"), out var subtaskId) || subtaskId == Guid.Empty)
            return new McpToolText(true, "knowledge_propose requires the server-assigned BEACON_SUBTASK_ID.");

        var targetKind = Text(args, "targetKind");
        if (!Enum.TryParse<ContentTargetKind>(targetKind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
            return new McpToolText(true, "targetKind must be a supported ContentTargetKind.");

        var operation = Text(args, "operation");
        if (string.IsNullOrWhiteSpace(operation))
            operation = ProposalOperation.Update.ToString();
        if (!Enum.TryParse<ProposalOperation>(operation, ignoreCase: true, out var parsedOperation)
            || !Enum.IsDefined(parsedOperation))
            return new McpToolText(true, "operation must be Create, Update or Delete.");

        var hasTargetId = TryGuid(args, "targetId", out var targetId) && targetId != Guid.Empty;
        var baseRevision = IntArg(args, "baseRevision");
        if (parsedOperation == ProposalOperation.Create)
        {
            if (kind != ContentTargetKind.Milestone || hasTargetId || baseRevision != 0)
                return new McpToolText(true,
                    "Create is supported for a new Milestone only: omit targetId and set baseRevision to 0.");
        }
        else
        {
            if (!hasTargetId)
                return new McpToolText(true, "Update and Delete require the exact existing targetId.");
            if (baseRevision is null or < 1)
                return new McpToolText(true, "Update and Delete require a positive current baseRevision.");
            if (parsedOperation == ProposalOperation.Delete
                && kind is not (ContentTargetKind.ContextSection or ContentTargetKind.Decision
                    or ContentTargetKind.Constraint or ContentTargetKind.Milestone))
                return new McpToolText(true,
                    "Delete is supported for ContextSection, Decision, Constraint and Milestone only.");
            if (parsedOperation == ProposalOperation.Delete && args?["payload"] is JsonObject deletePayload
                && deletePayload.Count != 0)
                return new McpToolText(true, "Delete proposals require an empty object payload.");
        }

        var rationale = Text(args, "rationaleMarkdown");
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Trim().Length > 1000)
            return new McpToolText(true, "rationaleMarkdown must contain 1-1000 characters.");

        if (args?["payload"] is not JsonObject payload)
            return new McpToolText(true, "payload must be a JSON object.");

        // The same Actor retrying the same target/base/payload gets the same key. A changed
        // payload or base revision gets a new key; the server still verifies the author/session.
        var normalizedRationale = rationale.Trim();
        var canonical = string.Join("\n",
            subtaskId.ToString("D"),
            kind.ToString(),
            targetId.ToString("D"),
            parsedOperation.ToString(),
            baseRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            normalizedRationale,
            payload.ToJsonString());
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var body = new JsonObject
        {
            ["targetKind"] = kind.ToString(),
            ["targetId"] = targetId.ToString("D"),
            ["operation"] = parsedOperation.ToString(),
            ["baseRevision"] = baseRevision.Value,
            ["payload"] = payload.DeepClone(),
            ["rationaleMarkdown"] = normalizedRationale,
            ["idempotencyKey"] = "knowledge-propose:" + digest
        };
        return await SendAsync(api, HttpMethod.Post, $"v1/projects/{projectId:D}/proposals", body, ct);
    }

    private static async Task<McpToolText> KnowledgeProposals(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var projectError))
            return new McpToolText(true, projectError);

        var status = Text(args, "status");
        var query = string.Empty;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ProposalStatus>(status, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
                return new McpToolText(true, "status must be a valid ProposalStatus.");
            query = "?status=" + Uri.EscapeDataString(parsed.ToString());
        }

        return await SendAsync(api, HttpMethod.Get,
            $"v1/projects/{projectId:D}/proposals/mine{query}", null, ct);
    }

    private static async Task<McpToolText> KnowledgeWithdraw(
        JsonObject? args, BeaconApiClient api, McpEnvironment env, CancellationToken ct)
    {
        if (!TryProject(env, args, out var projectId, out var projectError))
            return new McpToolText(true, projectError);
        if (!TryGuid(args, "proposalId", out var proposalId) || proposalId == Guid.Empty)
            return new McpToolText(true, "missing or invalid 'proposalId'.");

        return await SendAsync(api, HttpMethod.Post,
            $"v1/projects/{projectId:D}/proposals/{proposalId:D}/withdraw",
            new JsonObject(), ct);
    }
}
