namespace ProjectBeacon.Web.Tests.Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using RoadmapPage = ProjectBeacon.Web.Features.Roadmap.Roadmap;

/// <summary>
/// Renders the Roadmap page with the dialog provider normally supplied by MainLayout.
/// The Web.Tests project uses Microsoft.NET.Sdk, so this host is a C# component rather
/// than a Razor file.
/// </summary>
internal sealed class RoadmapDialogTestHost : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MudDialogProvider>(0);
        builder.CloseComponent();

        builder.OpenComponent<RoadmapPage>(1);
        builder.CloseComponent();
    }
}
