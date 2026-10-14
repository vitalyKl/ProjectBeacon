using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProjectBeacon.Web.Extensions;

/// <summary>
/// Revalidates the circuit's identity against live account/membership state on an
/// 60-second tick. Returning false makes the framework force a circuit sign-out
/// (AuthorizeRouteView re-evaluates and redirects); the cookie itself is untouched.
/// </summary>
public sealed class CircuitRevalidatingAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceProvider _scope;
    private readonly ILogger _logger;

    public CircuitRevalidatingAuthenticationStateProvider(IServiceProvider scope, ILoggerFactory loggerFactory)
        : base(loggerFactory)
    {
        _scope = scope;
        _logger = loggerFactory.CreateLogger<CircuitRevalidatingAuthenticationStateProvider>();
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(60);

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
        => CircuitTenantRevalidation.RevalidateAsync(_scope, authenticationState.User, _logger, cancellationToken);
}
