namespace ProjectBeacon.API.Endpoints;

using System.Reflection;

public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/version", () => Results.Ok(AppVersion.Current())).AllowAnonymous();
        return app;
    }
}

public static class AppVersion
{
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

    public static string Informational =>
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
        ?? "0";
}
