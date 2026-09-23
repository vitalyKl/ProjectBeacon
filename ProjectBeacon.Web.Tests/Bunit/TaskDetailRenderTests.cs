using ProjectBeacon.Domain.Enums;
using Bunit;
using Microsoft.EntityFrameworkCore;

namespace ProjectBeacon.Web.Tests.Bunit;

public sealed class TaskDetailRenderTests : BUnitRenderBase
{
  [Fact]
  public void TaskDetail_RendersWithTaskInfo()
  {
    // Arrange
    var task = DbContext.Tasks.First();

    // Act
    var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(builder => builder.Add(c => c.TaskId, task.Id));

    // Assert
    var html = cut.Markup;
    Assert.Contains(task.Title, html);
    Assert.Contains("<div", html);
  }

  [Fact]
  public void TaskDetail_RendersTaskTitle()
  {
    // Arrange
    var task = DbContext.Tasks.First();

    // Act
    var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(builder => builder.Add(c => c.TaskId, task.Id));

    // Assert
    var html = cut.Markup;
    Assert.Contains(task.Title, html);
  }

  [Fact]
  public void TaskDetail_RendersPageStructure()
  {
    // Arrange
    var task = DbContext.Tasks.First();

    // Act
    var cut = Bunit.RenderComponent<Features.Tasks.TaskDetail>(builder => builder.Add(c => c.TaskId, task.Id));

    // Assert
    var html = cut.Markup;
    Assert.Contains("task", html);
  }
}
