namespace ProjectBeacon.Infrastructure.Http;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Builds forwarded-header options while preserving the framework's loopback-only defaults
/// unless an explicit trusted-proxy/network list is configured.
/// </summary>
public static class ForwardedHeadersOptionsFactory
{
    public static ForwardedHeadersOptions Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardedProtoHeaderName = "X-Forwarded-Proto"
        };

        var section = configuration.GetSection("ForwardedHeaders");
        section.Bind(options);

        var knownProxies = Values(section, "KnownProxies").ToList();
        var knownNetworks = Values(section, "KnownNetworks").ToList();
        if (knownProxies.Count > 0 || knownNetworks.Count > 0)
        {
            // An explicitly configured trust list replaces, rather than extends, the
            // framework loopback defaults.
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            foreach (var value in knownProxies)
                options.KnownProxies.Add(IPAddress.Parse(value));
            foreach (var value in knownNetworks)
                options.KnownNetworks.Add(Microsoft.AspNetCore.HttpOverrides.IPNetwork.Parse(value));
        }

        return options;
    }

    private static IEnumerable<string> Values(IConfiguration section, string key)
    {
        var child = section.GetSection(key);
        if (!string.IsNullOrWhiteSpace(child.Value))
            yield return child.Value;

        foreach (var entry in child.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(entry.Value))
                yield return entry.Value;
        }
    }
}
