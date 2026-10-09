namespace ProjectBeacon.Cli.Client;

using System.Text.Json;

internal static class DaemonPayload
{
    public static string? ExtractSwapName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        var i = model.IndexOf('/');
        var name = i < 0 ? model : model[(i + 1)..];
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    public static string? ReadModelName(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("model", out var model) ? model.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? ReadPath(string payload) => ReadString(payload, "path");

    public static string BrowseRoot(WorkstationSettings settings, string payload)
    {
        var kind = ReadString(payload, "rootKind");
        var useModels = string.Equals(kind, "models", StringComparison.OrdinalIgnoreCase);
        return (useModels ? settings.ModelsRoot : settings.ProjectsRoot) ?? "";
    }

    public static string? ReadString(string payload, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
