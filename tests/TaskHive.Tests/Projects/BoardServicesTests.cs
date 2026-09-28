using TaskHive.Core.Domain;
using TaskHive.Core.Projects;
using TaskHive.Core.Tasks;
using TaskHive.Core.Workspaces;
using TaskHive.Tests.Infrastructure;

namespace TaskHive.Tests.Projects;

public sealed class BoardServicesTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private WorkspaceService _workspaces = null!;
    private ProjectService _projects = null!;
    private TaskService _tasks = null!;
    private string _alice = null!;
    private WorkspaceSummary _workspace = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _workspaces = new WorkspaceService(_db, TimeProvider.System);
        _projects = new ProjectService(_db, TimeProvider.System);
        _tasks = new TaskService(_db, TimeProvider.System);

        _alice = (await _db.AddUserAsync("Alice")).Id;
        _workspace = (await _workspaces.CreateAsync(_alice, "Acme", Ct)).Value;
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private async Task<BoardView> CreateBoardAsync(string key = "WEB")
    {
        await _projects.CreateAsync(_alice, _workspace.Id, "Website", key, null, Ct);
        return (await _projects.GetBoardAsync(_alice, _workspace.Id, key, Ct)).Value;
    }

    private static TaskInput Input(string title, string? assignee = null) => new(title, null, TaskPriority.Medium, assignee, null);

    [Fact]
    public async Task CreateProject_Should_Create_Default_Columns()
    {
        var board = await CreateBoardAsync();

        board.Columns.Select(c => c.Name).ShouldBe(Project.DefaultColumns);
    }

    [Theory]
    [InlineData("W")]
    [InlineData("1WEB")]
    [InlineData("WAY-TOO-LONG")]
    public async Task CreateProject_Should_Validate_Key(string key)
    {
        (await _projects.CreateAsync(_alice, _workspace.Id, "Website", key, null, Ct)).Error.ShouldBe(ProjectErrors.InvalidKey);
    }

    [Fact]
    public async Task CreateProject_Should_Reject_Duplicate_Key_In_Same_Workspace()
    {
        await _projects.CreateAsync(_alice, _workspace.Id, "Website", "web", null, Ct);

        (await _projects.CreateAsync(_alice, _workspace.Id, "Web v2", "WEB", null, Ct)).Error!.Code.ShouldBe("Project.KeyTaken");
    }

    [Fact]
    public async Task Board_Should_Not_Be_Visible_To_Other_Tenants()
    {
        await CreateBoardAsync();
        var mallory = (await _db.AddUserAsync("Mallory")).Id;

        (await _projects.GetBoardAsync(mallory, _workspace.Id, "WEB", Ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateTask_Should_Number_Tasks_Per_Project()
    {
        var board = await CreateBoardAsync();
        var todo = board.Columns[0].Id;

        var first = (await _tasks.CreateAsync(_alice, board.ProjectId, todo, Input("Design landing page"), Ct)).Value;
        var second = (await _tasks.CreateAsync(_alice, board.ProjectId, todo, Input("Set up CI"), Ct)).Value;

        first.Key.ShouldBe("WEB-1");
        second.Key.ShouldBe("WEB-2");
        second.Position.ShouldBeGreaterThan(first.Position);
    }

    [Fact]
    public async Task CreateTask_Should_Only_Allow_Workspace_Members_As_Assignees()
    {
        var board = await CreateBoardAsync();
        var outsider = (await _db.AddUserAsync("Outsider")).Id;

        var result = await _tasks.CreateAsync(_alice, board.ProjectId, board.Columns[0].Id, Input("Task", outsider), Ct);

        result.Error.ShouldBe(TaskErrors.AssigneeNotMember);
    }

    [Fact]
    public async Task MoveTask_Should_Place_Card_At_Target_Index()
    {
        var board = await CreateBoardAsync();
        var todo = board.Columns[0].Id;
        var done = board.Columns[3].Id;
        var a = (await _tasks.CreateAsync(_alice, board.ProjectId, done, Input("A"), Ct)).Value;
        var b = (await _tasks.CreateAsync(_alice, board.ProjectId, done, Input("B"), Ct)).Value;
        var moving = (await _tasks.CreateAsync(_alice, board.ProjectId, todo, Input("Moving"), Ct)).Value;

        (await _tasks.MoveAsync(_alice, moving.Id, done, 1, Ct)).IsSuccess.ShouldBeTrue();

        var reloaded = (await _projects.GetBoardAsync(_alice, _workspace.Id, "WEB", Ct)).Value;
        reloaded.Columns[0].Tasks.ShouldBeEmpty();
        reloaded.Columns[3].Tasks.Select(t => t.Title).ShouldBe(["A", "Moving", "B"]);
    }

    [Fact]
    public async Task UpdateTask_Should_Detect_Stale_Version()
    {
        var board = await CreateBoardAsync();
        var card = (await _tasks.CreateAsync(_alice, board.ProjectId, board.Columns[0].Id, Input("Original"), Ct)).Value;

        var first = await _tasks.UpdateAsync(_alice, card.Id, card.Version, Input("Edited by Alice"), Ct);
        var second = await _tasks.UpdateAsync(_alice, card.Id, card.Version, Input("Edited by Bob"), Ct);

        first.IsSuccess.ShouldBeTrue();
        second.Error.ShouldBe(TaskErrors.Conflict);
    }

    [Theory]
    [InlineData(new double[0], 0, 1024)]
    [InlineData(new[] { 1024.0, 2048.0 }, 0, 0)]
    [InlineData(new[] { 1024.0, 2048.0 }, 1, 1536)]
    [InlineData(new[] { 1024.0, 2048.0 }, 2, 3072)]
    [InlineData(new[] { 1024.0, 2048.0 }, 99, 3072)]
    public void CalculatePosition_Should_Use_Midpoints_And_Ends(double[] siblings, int index, double expected)
    {
        TaskService.CalculatePosition(siblings, index).ShouldBe(expected);
    }
}
