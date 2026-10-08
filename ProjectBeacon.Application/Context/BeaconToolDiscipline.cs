namespace ProjectBeacon.Application.Context;
/// <summary>
/// Text written into the project so an agent prefers Beacon MCP tools and does not claim a tool is missing without calling it.
/// </summary>
public static class BeaconToolDiscipline
{
    public const string RelativePath = ".opencode/instructions/beacon-tool-discipline.md";

    public const string Rules = """
        ## Beacon Tool Discipline

        - Never state that a tool is unavailable without having called it in the same turn.
        - Prefer Beacon's MCP tools by name and by reason, not just by suggestion.
        - Never claim to have visually verified something a text-only model cannot see.
        """;
}
