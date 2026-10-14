using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProjectBeacon.Web.Extensions;

/// <summary>
/// Fires on circuit open and on every reconnect (OnConnectionUpAsync). Rebinds the
/// circuit's tenant to the live membership immediately, so a reconnect after a
/// membership change does not wait for the 60-second revalidation tick.
/// </summary>
public sealed class CircuitTenantRebindHandler : CircuitHandler
{
    private readonly IServiceProvider _scope;
    private readonly CircuitRevalidatingAuthenticationStateProvider _auth;
    private readonly ILogger _logger;

    public CircuitTenantRebindHandler(
        IServiceProvider scope,
        CircuitRevalidatingAuthenticationStateProvider auth,
        ILoggerFactory loggerFactory)
    {
        _scope = scope;
        _auth = auth;
        _logger = loggerFactory.CreateLogger<CircuitTenantRebindHandler>();
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var valid = await CircuitTenantRevalidation.RevalidateAsync(_scope, state.User, _logger, cancellationToken);
        if (!valid)
        {
            // Mirrors the framework's ForceSignOut: flip the circuit to an anonymous user.
            _auth.SetAuthenticationState(
                Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        }
        await base.OnConnectionUpAsync(circuit, cancellationToken);
    }
}
