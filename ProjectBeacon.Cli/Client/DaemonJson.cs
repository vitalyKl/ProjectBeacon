namespace ProjectBeacon.Cli.Client;

using System.Text.Json;
using System.Text.Json.Serialization;

internal static class DaemonJson
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
