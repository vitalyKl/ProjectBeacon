using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace ProjectBeacon.Web.Tests.Bunit;

/// <summary>
/// P1.5: when the circuit's authentication state is flipped to unauthenticated
/// (framework ForceSignOut after revalidation, or anonymous principal pushed on
/// reconnect), the UI re-evaluates and shows the unauthenticated view — the
/// long-lived circuit stops presenting authenticated chrome.
/// </summary>
public sealed class CircuitSignOutRenderTests : BUnitRenderBase
{
    public CircuitSignOutRenderTests()
    {
        // AuthorizeView needs a policy provider and an authorization service.
        Bunit.Services.AddAuthorizationCore();
        Bunit.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationService, StubAuthorizationService>();
    }

    /// <summary>Default-policy semantics: authorized iff the user is authenticated.</summary>
    private sealed class StubAuthorizationService : Microsoft.AspNetCore.Authorization.IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            System.Security.Claims.ClaimsPrincipal? user, object? resource,
            System.Collections.Generic.IEnumerable<IAuthorizationRequirement>? requirements)
            => Task.FromResult(Result(user));

        public Task<AuthorizationResult> AuthorizeAsync(
            System.Security.Claims.ClaimsPrincipal? user, object? resource, string? policyName)
            => Task.FromResult(Result(user));

        private static AuthorizationResult Result(System.Security.Claims.ClaimsPrincipal? user)
            => user?.Identity?.IsAuthenticated == true
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed();
    }

    [Fact]
    public void ForceSignOutFlipsCircuit_AuthorizeViewShowsUnauthenticated()
    {
        var cut = Bunit.RenderComponent<AuthGate>();
        Assert.Contains("AUTH-GRANTED", cut.Markup);

        var fake = Bunit.Services.GetRequiredService<FakeAuthenticationStateProvider>();
        fake.TriggerUnauthenticationStateChanged();

        cut.WaitForAssertion(() => Assert.Contains("AUTH-DENIED", cut.Markup));
        cut.WaitForAssertion(() => Assert.DoesNotContain("AUTH-GRANTED", cut.Markup));
    }

    private sealed class AuthGate : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingAuthenticationState>(0);
            builder.AddAttribute(1, nameof(CascadingAuthenticationState.ChildContent), Gate);
            builder.CloseComponent();
        }

        private static RenderFragment Gate = b =>
        {
            // A plain AuthorizeView authorizes everyone (GetAuthorizeData() is null),
            // so a role requirement is needed for the view to consult the authorization service.
            b.OpenComponent<AuthorizeView>(2);
            b.AddAttribute(3, nameof(AuthorizeView.Roles), "tester");
            b.AddAttribute(4, nameof(AuthorizeView.Authorized), AuthorizedView);
            b.AddAttribute(5, nameof(AuthorizeView.NotAuthorized), NotAuthorizedView);
            b.CloseComponent();
        };

        private static RenderFragment<AuthenticationState> AuthorizedView = _ => b => b.AddContent(0, "AUTH-GRANTED");
        private static RenderFragment<AuthenticationState> NotAuthorizedView = _ => b => b.AddContent(0, "AUTH-DENIED");
    }
}
