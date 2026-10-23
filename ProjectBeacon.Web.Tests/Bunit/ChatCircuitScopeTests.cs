using ProjectBeacon.Web.Features.Chat;
using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TestContext = Bunit.TestContext;

namespace ProjectBeacon.Web.Tests.Bunit;

/// <summary>
/// The production app registers only IHttpContextAccessor, never a bare HttpContext instance, and
/// the chat components take their actor from the authentication principal. The fixture must keep
/// no HttpContext registration of its own, and the components must still construct and render
/// without one.
/// </summary>
public sealed class ChatCircuitScopeTests : BUnitRenderBase
{
    private static void AssertNoHttpContext(TestContext context)
    {
        var descriptor = context.Services.FirstOrDefault(d => d.ServiceType == typeof(HttpContext));
        Assert.Null(descriptor);
    }

    [Fact]
    public void ChatPage_RendersWithoutHttpContextInCircuitScope()
    {
        AssertNoHttpContext(Bunit);
        var cut = Bunit.RenderComponent<Chat>();
        Assert.Contains("beacon-chat-page", cut.Markup);
    }

    [Fact]
    public void ChatDock_RendersFabWithoutHttpContextInCircuitScope()
    {
        AssertNoHttpContext(Bunit);
        Bunit.Services.GetRequiredService<NavigationManager>().NavigateTo("/dashboard");
        var cut = Bunit.RenderComponent<ChatDock>();
        Assert.Contains("beacon-chat-fab", cut.Markup);
    }
}
