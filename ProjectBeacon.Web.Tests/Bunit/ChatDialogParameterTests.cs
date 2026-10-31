namespace ProjectBeacon.Web.Tests.Bunit;

using global::Bunit;
using ProjectBeacon.Application.Agents;
using ProjectBeacon.Domain.Enums;
using ProjectBeacon.Web.Features.Chat;

public sealed class ChatDialogParameterTests : BUnitRenderBase
{
    [Fact]
    public void ModelDialogAcceptsModelSettingsAsComponentParameters()
    {
        var backend = new LocalModelBackendDto(
            Guid.NewGuid(), "dialog-model", ModelBackendType.LlamaCpp,
            "llama-server -m model.gguf", 8192, 30, [], Guid.NewGuid(), null, false, "");

        var cut = Bunit.RenderComponent<ChatModelSettingsDialog>(parameters => parameters
            .Add(p => p.Models, new List<LocalModelBackendDto> { backend })
            .Add(p => p.Model, "beacon-local/dialog-model")
            .Add(p => p.ReasoningEffort, "medium"));

        Assert.Contains("dialog-model", cut.Markup);
        Assert.Contains(backend.ContextSize.ToString("N0"), cut.Markup);
    }

    [Fact]
    public void RenameDialogAcceptsSessionAsComponentParameter()
    {
        var session = new ProjectBeacon.Application.Chat.ChatSessionDto(
            Guid.NewGuid(), Guid.NewGuid(), "Project", Guid.NewGuid(), "external-1",
            "Existing chat title", ProjectBeacon.Domain.Enums.ChatSessionStatus.Idle,
            DateTime.UtcNow, null);

        var cut = Bunit.RenderComponent<RenameChatSessionDialog>(parameters => parameters
            .Add(p => p.Session, session));

        Assert.Contains("Existing chat title", cut.Markup);
        Assert.Single(cut.FindAll("input"));
    }
}
