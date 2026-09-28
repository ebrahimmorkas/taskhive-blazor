using TaskHive.Core.Domain;
using TaskHive.Core.Projects;
using TaskHive.Core.Realtime;
using TaskHive.Core.Tasks;
using TaskHive.Core.Workspaces;
using TaskHive.Tests.Infrastructure;

namespace TaskHive.Tests.Realtime;

public sealed class RealtimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BoardChange Change(Guid projectId) =>
        new(projectId, BoardChangeKind.TaskMoved, Guid.NewGuid(), "WEB-1", "user-1", "Alice");

    [Fact]
    public async Task Notifier_Should_Deliver_Only_To_Subscribers_Of_The_Same_Project()
    {
        var notifier = new InMemoryBoardNotifier();
        var projectA = Guid.NewGuid();
        var received = new List<string>();
        using var a = notifier.Subscribe(projectA, _ => { received.Add("A"); return Task.CompletedTask; });
        using var b = notifier.Subscribe(Guid.NewGuid(), _ => { received.Add("B"); return Task.CompletedTask; });

        await notifier.PublishAsync(Change(projectA));

        received.ShouldBe(["A"]);
    }

    [Fact]
    public async Task Notifier_Should_Stop_Delivering_After_Unsubscribe()
    {
        var notifier = new InMemoryBoardNotifier();
        var projectId = Guid.NewGuid();
        var count = 0;
        var subscription = notifier.Subscribe(projectId, _ => { count++; return Task.CompletedTask; });

        await notifier.PublishAsync(Change(projectId));
        subscription.Dispose();
        await notifier.PublishAsync(Change(projectId));

        count.ShouldBe(1);
    }

    [Fact]
    public async Task Notifier_Should_Isolate_Failing_Subscribers()
    {
        var notifier = new InMemoryBoardNotifier();
        var projectId = Guid.NewGuid();
        var delivered = false;
        using var broken = notifier.Subscribe(projectId, _ => throw new ObjectDisposedException("circuit"));
        using var healthy = notifier.Subscribe(projectId, _ => { delivered = true; return Task.CompletedTask; });

        await notifier.PublishAsync(Change(projectId));

        delivered.ShouldBeTrue();
    }

    [Fact]
    public async Task Presence_Should_Count_Users_Once_Across_Tabs()
    {
        var tracker = new PresenceTracker();
        var projectId = Guid.NewGuid();
        var alice = new PresentUser("1", "Alice");

        var tab1 = await tracker.JoinAsync(projectId, alice);
        var tab2 = await tracker.JoinAsync(projectId, alice);
        using var bob = await tracker.JoinAsync(projectId, new PresentUser("2", "Bob"));

        tracker.GetViewers(projectId).Select(u => u.DisplayName).ShouldBe(["Alice", "Bob"]);

        tab1.Dispose();
        tracker.GetViewers(projectId).Count.ShouldBe(2);

        tab2.Dispose();
        await Task.Delay(50, Ct);
        tracker.GetViewers(projectId).ShouldHaveSingleItem().DisplayName.ShouldBe("Bob");
    }

    [Fact]
    public async Task Presence_Should_Raise_Change_Events()
    {
        var tracker = new PresenceTracker();
        var projectId = Guid.NewGuid();
        var changes = 0;
        tracker.PresenceChanged += id => { if (id == projectId) { changes++; } return Task.CompletedTask; };

        var session = await tracker.JoinAsync(projectId, new PresentUser("1", "Alice"));
        session.Dispose();
        await Task.Delay(50, Ct);

        changes.ShouldBe(2);
    }

    [Fact]
    public async Task TaskService_Should_Publish_Changes_With_Actor_Name()
    {
        await using var db = await TestDatabase.CreateAsync();
        var notifier = new InMemoryBoardNotifier();
        var alice = await db.AddUserAsync("Alice Smith");
        var workspace = (await new WorkspaceService(db, TimeProvider.System).CreateAsync(alice.Id, "Acme", Ct)).Value;
        var projects = new ProjectService(db, TimeProvider.System);
        await projects.CreateAsync(alice.Id, workspace.Id, "Website", "WEB", null, Ct);
        var board = (await projects.GetBoardAsync(alice.Id, workspace.Id, "WEB", Ct)).Value;
        var tasks = new TaskService(db, TimeProvider.System, notifier);

        var changes = new List<BoardChange>();
        using var _ = notifier.Subscribe(board.ProjectId, change => { changes.Add(change); return Task.CompletedTask; });

        var card = (await tasks.CreateAsync(alice.Id, board.ProjectId, board.Columns[0].Id, new TaskInput("Task", null, TaskPriority.Low, null, null), Ct)).Value;
        await tasks.MoveAsync(alice.Id, card.Id, board.Columns[1].Id, 0, Ct);
        await tasks.DeleteAsync(alice.Id, card.Id, Ct);

        changes.Select(c => c.Kind).ShouldBe([BoardChangeKind.TaskCreated, BoardChangeKind.TaskMoved, BoardChangeKind.TaskDeleted]);
        changes.ShouldAllBe(c => c.ActorName == "Alice Smith" && c.TaskKey == "WEB-1");
    }
}
