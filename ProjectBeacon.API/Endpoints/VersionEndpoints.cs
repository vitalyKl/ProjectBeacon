namespace ProjectBeacon.API.Endpoints;

using ProjectBeacon.API;

using System.Reflection;

/// <summary>
/// Version route for the API host.
/// </summary>
public static class VersionEndpoints
{
    /// <summary>
    /// Maps <c>GET /v1/version</c> with <c>AllowAnonymous</c>.
    /// <c>version</c> is the assembly informational version; <c>gitSha</c> is <c>BEACON_GIT_SHA</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/version", () => Results.Ok(AppVersion.Current())).AllowAnonymous();
        return app;
    }
}

/// <summary>
/// Version payload for <c>GET /v1/version</c>.
/// <c>version</c> is the assembly informational version; <c>gitSha</c> is <c>BEACON_GIT_SHA</c>.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Returns <c>version</c> from <c>AssemblyInformationalVersionAttribute</c> (else the assembly version, else <c>0</c>) and <c>gitSha</c> from <c>BEACON_GIT_SHA</c>.
    /// </summary>
    public static object Current()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0";
        return new
        {
            version = informational,
            gitSha = Environment.GetEnvironmentVariable("BEACON_GIT_SHA")
        };
    }

    /// <summary>
    /// Assembly informational version, or the assembly version, or <c>0</c>.
    /// </summary>
    public static string Informational =>
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
        ?? "0";
}
