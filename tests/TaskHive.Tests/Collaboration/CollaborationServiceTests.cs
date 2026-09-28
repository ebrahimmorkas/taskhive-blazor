using Microsoft.Extensions.Time.Testing;
using TaskHive.Core.Collaboration;
using TaskHive.Core.Domain;
using TaskHive.Core.Projects;
using TaskHive.Core.Realtime;
using TaskHive.Core.Tasks;
using TaskHive.Core.Workspaces;
using TaskHive.Tests.Infrastructure;

namespace TaskHive.Tests.Collaboration;

public sealed class CollaborationServiceTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero));
    private TestDatabase _db = null!;
    private CollaborationService _service = null!;
    private TaskService _tasks = null!;
    private string _alice = null!;
    private WorkspaceSummary _workspace = null!;
    private BoardView _board = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _service = new CollaborationService(_db, _clock);
        _tasks = new TaskService(_db, _clock, new InMemoryBoardNotifier());

        _alice = (await _db.AddUserAsync("Alice")).Id;
        _workspace = (await new WorkspaceService(_db, _clock).CreateAsync(_alice, "Acme", Ct)).Value;
        var projects = new ProjectService(_db, _clock);
        await projects.CreateAsync(_alice, _workspace.Id, "Website", "WEB", null, Ct);
        _board = (await projects.GetBoardAsync(_alice, _workspace.Id, "WEB", Ct)).Value;
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private async Task<TaskCardView> CreateTaskAsync(string title, int column = 0, string? assignee = null, DateOnly? due = null, TaskPriority priority = TaskPriority.Medium) =>
        (await _tasks.CreateAsync(_alice, _board.ProjectId, _board.Columns[column].Id, new TaskInput(title, null, priority, assignee, due), Ct)).Value;

    [Fact]
    public async Task Comments_Should_Be_Returned_In_Order_With_Author()
    {
        var task = await CreateTaskAsync("Write copy");

        await _service.AddCommentAsync(_alice, task.Id, "First draft ready", Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _service.AddCommentAsync(_alice, task.Id, "Updated after review", Ct);

        var comments = (await _service.GetCommentsAsync(_alice, task.Id, Ct)).Value;
        comments.Select(c => c.Body).ShouldBe(["First draft ready", "Updated after review"]);
        comments.ShouldAllBe(c => c.AuthorName == "Alice");
    }

    [Fact]
    public async Task Comments_Should_Be_Validated_And_Tenant_Isolated()
    {
        var task = await CreateTaskAsync("Secret");
        var mallory = (await _db.AddUserAsync("Mallory")).Id;

        (await _service.AddCommentAsync(_alice, task.Id, "   ", Ct)).Error.ShouldBe(CollaborationErrors.InvalidComment);
        (await _service.AddCommentAsync(mallory, task.Id, "Hi", Ct)).Error.ShouldBe(TaskErrors.NotFound);
        (await _service.GetCommentsAsync(mallory, task.Id, Ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Activity_Feed_Should_List_Newest_First()
    {
        var task = await CreateTaskAsync("Launch");
        await _tasks.MoveAsync(_alice, task.Id, _board.Columns[1].Id, 0, Ct);
        await _service.AddCommentAsync(_alice, task.Id, "On it", Ct);

        var activity = (await _service.GetActivityAsync(_alice, _workspace.Id, cancellationToken: Ct)).Value;

        activity[0].Message.ShouldBe("commented on WEB-1");
        activity[1].Message.ShouldBe("moved WEB-1 to In progress");
        activity.Last().Message.ShouldContain("created the workspace");
    }

    [Fact]
    public async Task MyTasks_Should_Exclude_Done_And_Others_And_Sort_Overdue_First()
    {
        var bob = (await _db.AddUserAsync("Bob")).Id;
        await new WorkspaceService(_db, _clock).AddMemberAsync(_alice, _workspace.Id, "bob@example.com", WorkspaceRole.Member, Ct);

        await CreateTaskAsync("Due later", assignee: _alice, due: Today.AddDays(5));
        await CreateTaskAsync("Overdue", assignee: _alice, due: Today.AddDays(-2));
        await CreateTaskAsync("No date", assignee: _alice, priority: TaskPriority.Urgent);
        await CreateTaskAsync("Finished", column: 3, assignee: _alice);
        await CreateTaskAsync("Bob's task", assignee: bob);

        var mine = await _service.GetMyTasksAsync(_alice, Ct);

        mine.Select(t => t.Title).ShouldBe(["Overdue", "Due later", "No date"]);
        mine[0].IsOverdue.ShouldBeTrue();
        mine[0].WorkspaceSlug.ShouldBe("acme");
    }
}
