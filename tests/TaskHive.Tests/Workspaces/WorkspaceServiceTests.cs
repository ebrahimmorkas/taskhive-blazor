using TaskHive.Core.Domain;
using TaskHive.Core.Workspaces;
using TaskHive.Tests.Infrastructure;

namespace TaskHive.Tests.Workspaces;

public sealed class WorkspaceServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private WorkspaceService _service = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _service = new WorkspaceService(_db, TimeProvider.System);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task Create_Should_Make_Creator_Owner_And_Generate_Slug()
    {
        var alice = await _db.AddUserAsync("Alice");

        var workspace = (await _service.CreateAsync(alice.Id, "Acme Corp!", Ct)).Value;

        workspace.Slug.ShouldBe("acme-corp");
        workspace.Role.ShouldBe(WorkspaceRole.Owner);
        (await _service.GetForUserAsync(alice.Id, Ct)).ShouldHaveSingleItem().MemberCount.ShouldBe(1);
    }

    [Fact]
    public async Task Create_Should_Keep_Slugs_Unique_Across_Tenants()
    {
        var alice = await _db.AddUserAsync("Alice");
        var bob = await _db.AddUserAsync("Bob");

        var first = (await _service.CreateAsync(alice.Id, "Design Team", Ct)).Value;
        var second = (await _service.CreateAsync(bob.Id, "Design Team", Ct)).Value;

        first.Slug.ShouldBe("design-team");
        second.Slug.ShouldBe("design-team-2");
    }

    [Fact]
    public async Task GetBySlug_Should_Hide_Workspace_From_Non_Members()
    {
        var alice = await _db.AddUserAsync("Alice");
        var mallory = await _db.AddUserAsync("Mallory");
        var workspace = (await _service.CreateAsync(alice.Id, "Secret Project", Ct)).Value;

        var result = await _service.GetBySlugAsync(mallory.Id, workspace.Slug, Ct);

        result.Error.ShouldBe(WorkspaceErrors.NotFound);
    }

    [Fact]
    public async Task Owner_Can_Add_Existing_User_As_Member()
    {
        var alice = await _db.AddUserAsync("Alice");
        var bob = await _db.AddUserAsync("Bob");
        var workspace = (await _service.CreateAsync(alice.Id, "Acme", Ct)).Value;

        (await _service.AddMemberAsync(alice.Id, workspace.Id, "BOB@example.com", WorkspaceRole.Member, Ct)).IsSuccess.ShouldBeTrue();

        (await _service.GetBySlugAsync(bob.Id, workspace.Slug, Ct)).Value.Role.ShouldBe(WorkspaceRole.Member);
        (await _service.GetMembersAsync(alice.Id, workspace.Id, Ct)).Value.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Regular_Member_Cannot_Add_Members()
    {
        var alice = await _db.AddUserAsync("Alice");
        var bob = await _db.AddUserAsync("Bob");
        await _db.AddUserAsync("Carol");
        var workspace = (await _service.CreateAsync(alice.Id, "Acme", Ct)).Value;
        await _service.AddMemberAsync(alice.Id, workspace.Id, "bob@example.com", WorkspaceRole.Member, Ct);

        var result = await _service.AddMemberAsync(bob.Id, workspace.Id, "carol@example.com", WorkspaceRole.Member, Ct);

        result.Error.ShouldBe(WorkspaceErrors.NotAllowed);
    }

    [Fact]
    public async Task AddMember_Should_Reject_Unknown_Email_And_Duplicates()
    {
        var alice = await _db.AddUserAsync("Alice");
        await _db.AddUserAsync("Bob");
        var workspace = (await _service.CreateAsync(alice.Id, "Acme", Ct)).Value;
        await _service.AddMemberAsync(alice.Id, workspace.Id, "bob@example.com", WorkspaceRole.Member, Ct);

        (await _service.AddMemberAsync(alice.Id, workspace.Id, "ghost@example.com", WorkspaceRole.Member, Ct)).Error!.Code.ShouldBe("Workspace.UserNotFound");
        (await _service.AddMemberAsync(alice.Id, workspace.Id, "bob@example.com", WorkspaceRole.Admin, Ct)).Error!.Code.ShouldBe("Workspace.AlreadyMember");
        (await _service.AddMemberAsync(alice.Id, workspace.Id, "bob@example.com", WorkspaceRole.Owner, Ct)).Error.ShouldBe(WorkspaceErrors.CannotAssignOwner);
    }

    [Fact]
    public async Task Owner_Cannot_Be_Removed_But_Members_Can_Leave()
    {
        var alice = await _db.AddUserAsync("Alice");
        var bob = await _db.AddUserAsync("Bob");
        var workspace = (await _service.CreateAsync(alice.Id, "Acme", Ct)).Value;
        await _service.AddMemberAsync(alice.Id, workspace.Id, "bob@example.com", WorkspaceRole.Admin, Ct);

        (await _service.RemoveMemberAsync(bob.Id, workspace.Id, alice.Id, Ct)).Error.ShouldBe(WorkspaceErrors.CannotRemoveOwner);
        (await _service.RemoveMemberAsync(bob.Id, workspace.Id, bob.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await _service.GetForUserAsync(bob.Id, Ct)).ShouldBeEmpty();
    }
}
