namespace ProjectBeacon.API.Auth;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

/// <summary>A normalized row in the generated /v1 route authorization inventory.</summary>
public sealed record EndpointAuthorizationRoute(string Method, string Route, string Policy);

/// <summary>
/// Builds an authorization matrix from the mapped endpoint metadata so the inventory cannot
/// silently drift from route declarations. Handler-level resource checks remain outside this
/// route-level matrix.
/// </summary>
public static class EndpointAuthorizationInventory
{
    public static IReadOnlyList<EndpointAuthorizationRoute> Create(EndpointDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint =>
            {
                var route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "/";
                if (!route.StartsWith("/v1/", StringComparison.Ordinal))
                    return null;

                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                var method = methods is { Count: > 0 }
                    ? string.Join(", ", methods.OrderBy(value => value, StringComparer.Ordinal))
                    : "*";

                return new EndpointAuthorizationRoute(
                    method,
                    route,
                    DescribePolicy(endpoint));
            })
            .Where(route => route is not null)
            .Select(route => route!)
            .OrderBy(route => route.Route, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToArray();
    }

    public static string ToMarkdown(IEnumerable<EndpointAuthorizationRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var lines = new List<string>
        {
            "| Method | Route | Route policy |",
            "|---|---|---|"
        };

        foreach (var route in routes.OrderBy(route => route.Route, StringComparer.Ordinal)
                     .ThenBy(route => route.Method, StringComparer.Ordinal))
        {
            lines.Add($"| {Escape(route.Method)} | `{Escape(route.Route)}` | {Escape(route.Policy)} |");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribePolicy(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return "Anonymous";

        var actor = endpoint.Metadata.GetMetadata<RouteActorRequirementMetadata>();
        if (actor is not null)
        {
            var label = actor.Requirement switch
            {
                RouteActorRequirement.Human => "Human",
                RouteActorRequirement.Device => "Device",
                RouteActorRequirement.ApiToken => "API token",
                RouteActorRequirement.HumanOrApiToken => "Human or API token",
                _ => "Unknown actor"
            };

            return actor.Capability is { } capability
                ? $"{label} ({capability})"
                : label;
        }

        // A bare RequireAuthorization() does not say which actor may use the route.
        // Only anonymous or typed actor metadata is a complete route classification.
        return "UNCLASSIFIED";
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);
}
