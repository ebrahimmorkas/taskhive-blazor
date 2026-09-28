using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using TaskHive.Core.Domain;
using TaskHive.Core.Projects;
using TaskHive.Web.Components.Board;
using TaskHive.Web.Components.Shared;

namespace TaskHive.Web.Tests;

public sealed class BoardComponentTests : BunitContext
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    public BoardComponentTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static TaskCardView Card(
        TaskPriority priority = TaskPriority.Medium,
        string? assignee = null,
        DateOnly? due = null) =>
        new(Guid.NewGuid(), "WEB-7", "Ship the landing page", null, priority, assignee is null ? null : "user-1", assignee,
            due, Guid.NewGuid(), 1024, Guid.NewGuid());

    [Fact]
    public void TaskCard_Should_Render_Key_Title_And_Priority_Class()
    {
        var cut = Render<TaskCard>(p => p.Add(x => x.Card, Card(TaskPriority.Urgent)).Add(x => x.Today, Today));

        cut.Find(".task-key").TextContent.ShouldBe("WEB-7");
        cut.Find(".task-title").TextContent.ShouldBe("Ship the landing page");
        cut.Find(".task-card").ClassList.ShouldContain("priority-urgent");
    }

    [Fact]
    public void TaskCard_Should_Flag_Overdue_Tasks()
    {
        var cut = Render<TaskCard>(p => p.Add(x => x.Card, Card(due: Today.AddDays(-1))).Add(x => x.Today, Today));

        cut.Find(".due-date").TextContent.ShouldContain("Overdue");
    }

    [Fact]
    public void TaskCard_Should_Not_Flag_Future_Due_Date()
    {
        var cut = Render<TaskCard>(p => p.Add(x => x.Card, Card(due: Today.AddDays(3))).Add(x => x.Today, Today));

        cut.Find(".due-date").TextContent.ShouldNotContain("Overdue");
    }

    [Fact]
    public void TaskCard_Should_Show_Assignee_Initials()
    {
        var cut = Render<TaskCard>(p => p.Add(x => x.Card, Card(assignee: "Jane Doe")).Add(x => x.Today, Today));

        cut.FindComponent<UserAvatar>().Find(".mud-avatar").TextContent.Trim().ShouldBe("JD");
    }

    [Fact]
    public async Task TaskCard_Click_Should_Raise_OnOpen_With_Card()
    {
        TaskCardView? opened = null;
        var card = Card();
        var cut = Render<TaskCard>(p => p
            .Add(x => x.Card, card)
            .Add(x => x.Today, Today)
            .Add(x => x.OnOpen, (TaskCardView c) => opened = c));

        await cut.Find(".task-card").ClickAsync(new());

        opened.ShouldBe(card);
    }

    [Theory]
    [InlineData(TaskPriority.Urgent, "mud-chip-color-error")]
    [InlineData(TaskPriority.High, "mud-chip-color-warning")]
    [InlineData(TaskPriority.Low, "mud-chip-color-default")]
    public void PriorityChip_Should_Use_Colour_Per_Priority(TaskPriority priority, string expectedClass)
    {
        var cut = Render<PriorityChip>(p => p.Add(x => x.Priority, priority));

        cut.Find(".mud-chip").ClassList.ShouldContain(expectedClass);
    }
}
