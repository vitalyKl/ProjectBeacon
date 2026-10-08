using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ProjectBeacon.Infrastructure.Http;
/// <summary>
/// Writes an RFC 7807 problem+json body and a trace id.
/// </summary>
public static class ProblemJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string TraceId(HttpContext context) =>
        Activity.Current?.Id ?? context.TraceIdentifier;

    public static async Task WriteAsync(
        HttpContext context, int status, string title, string type, string? detail, CancellationToken ct = default)
    {
        if (context.Response.HasStarted)
            return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var problem = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["title"] = title,
            ["status"] = status,
            ["traceId"] = TraceId(context)
        };
        if (detail is not null)
            problem["detail"] = detail;
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions), ct);
    }
}
