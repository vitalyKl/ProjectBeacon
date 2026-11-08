namespace ProjectBeacon.Domain.Entities.Agents;

using System.Text.Json;
using ProjectBeacon.Domain.Common;

/// <summary>
/// A per-user override cloned from a built-in subtask profile. Built-ins remain code-owned;
/// a row with the same key overrides the built-in for this user only.
/// </summary>
public sealed class AgentProfile : Entity
{
    public AgentProfile() { }

    public Guid UserId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Instruction { get; private set; } = string.Empty;
    public string Mode { get; private set; } = "Act";
    public string ToolsJson { get; private set; } = "[]";
    public string PathPolicy { get; private set; } = "FromPlanner";
    public string DefaultPathsJson { get; private set; } = "[]";
    public bool CommandsEnabled { get; private set; }
    public bool ReportsDiff { get; private set; }
    public string SizeHint { get; private set; } = string.Empty;
    public string Effort { get; private set; } = "medium";
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public IReadOnlyList<string> Tools => Deserialize(ToolsJson);
    public IReadOnlyList<string> DefaultPaths => Deserialize(DefaultPathsJson);

    public static AgentProfile Create(
        Guid userId, string key, string name, string description, string instruction,
        string mode, IReadOnlyList<string> tools, string pathPolicy,
        IReadOnlyList<string> defaultPaths, bool commandsEnabled, bool reportsDiff,
        string sizeHint, string effort)
    {
        var now = DateTime.UtcNow;
        var profile = Entity.New<AgentProfile>();
        profile.UserId = userId;
        profile.Key = key;
        profile.CreatedAt = now;
        profile.Update(name, description, instruction, mode, tools, pathPolicy, defaultPaths,
            commandsEnabled, reportsDiff, sizeHint, effort);
        return profile;
    }

    public void Update(
        string name, string description, string instruction, string mode,
        IReadOnlyList<string> tools, string pathPolicy, IReadOnlyList<string> defaultPaths,
        bool commandsEnabled, bool reportsDiff, string sizeHint, string effort)
    {
        Name = name.Trim();
        Description = description.Trim();
        Instruction = instruction.Trim();
        Mode = mode;
        ToolsJson = JsonSerializer.Serialize(tools.Distinct(StringComparer.Ordinal).ToArray());
        PathPolicy = pathPolicy;
        DefaultPathsJson = JsonSerializer.Serialize(defaultPaths.Distinct(StringComparer.Ordinal).ToArray());
        CommandsEnabled = commandsEnabled;
        ReportsDiff = reportsDiff;
        SizeHint = sizeHint.Trim();
        Effort = effort;
        UpdatedAt = DateTime.UtcNow;
    }

    private static IReadOnlyList<string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
