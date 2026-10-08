namespace ProjectBeacon.Cli.Mcp;

using System.Text.Json.Nodes;

internal static class McpSchema
{
    public static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema
    };

    public static JsonObject Props(params (string Name, string Type, bool Required)[] fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in fields)
        {
            properties[field.Name] = new JsonObject { ["type"] = field.Type };
            if (field.Required)
                required.Add(field.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }
}
