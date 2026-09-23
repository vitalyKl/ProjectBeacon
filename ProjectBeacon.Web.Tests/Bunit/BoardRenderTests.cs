using Bunit;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class BoardRenderTests : BUnitRenderBase
{
  [Fact]
  public void Board_RendersBoardContent()
  {
    // Act
    var cut = Bunit.RenderComponent<Features.Board.Board>();

    // Assert
    var html = cut.Markup;
    Assert.Contains("kanban-board", html);
    Assert.Contains("Task One", html);
    Assert.Contains("Task Two", html);
    Assert.Contains("Task Three", html);
  }

  [Fact]
  public void Board_RendersColumns()
  {
    // Act
    var cut = Bunit.RenderComponent<Features.Board.Board>();

    // Assert
    var columns = cut.FindAll(".kanban-column");
    Assert.NotEmpty(columns);
  }

  [Fact]
  public void Board_RendersTaskCards()
  {
    // Act
    var cut = Bunit.RenderComponent<Features.Board.Board>();

    // Assert
    var cards = cut.FindAll(".beacon-board-card");
    Assert.NotEmpty(cards);
  }
}
