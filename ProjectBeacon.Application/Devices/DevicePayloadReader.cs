namespace ProjectBeacon.Application.Devices;

using System.Text.Json;

internal static class DevicePayloadReader
{
    public static Guid? ReadGuid(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var id))
                return id;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static int ReadInt(JsonElement parent, string name) => ReadNullableInt(parent, name) ?? 0;

    public static int? ReadNullableInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var number))
            return number;
        return null;
    }

    public static double? ReadDouble(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed) ? parsed : null;
    }

    public static long? ReadLong(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed) ? parsed : null;
    }
}
