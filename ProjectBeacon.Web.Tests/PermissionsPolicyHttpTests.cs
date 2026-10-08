namespace ProjectBeacon.Web.Tests;

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

public sealed class PermissionsPolicyHttpTests
{
    [Fact]
    public async Task Document_AllowsUnloadForBlazorCircuit()
    {
        await using var factory = new WebTestFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Permissions-Policy", out var values));
        Assert.Contains(values!, value => value.Contains("unload=(self)", StringComparison.Ordinal));
    }
}
