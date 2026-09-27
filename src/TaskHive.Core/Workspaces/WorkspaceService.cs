using Microsoft.EntityFrameworkCore;
using TaskHive.Core.Common;
using TaskHive.Core.Data;
using TaskHive.Core.Domain;

namespace TaskHive.Core.Workspaces;

public sealed record WorkspaceSummary(Guid Id, string Name, string Slug, WorkspaceRole Role, int MemberCount, int ProjectCount);

public sealed record MemberInfo(string UserId, string DisplayName, string Email, WorkspaceRole Role, DateTime JoinedAtUtc);

/// <summary>
/// Workspace (tenant) management. Every operation verifies the caller's membership, so one tenant
/// can never read or change another tenant's data even with a guessed id or slug.
/// </summary>
public sealed class WorkspaceService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider timeProvider)
{
    public async Task<Result<WorkspaceSummary>> CreateAsync(string userId, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
        {
            return WorkspaceErrors.InvalidName;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var workspace = Workspace.Create(name, userId, timeProvider.GetUtcNow().UtcDateTime);
        var baseSlug = workspace.Slug;
        var suffix = 1;
        while (await db.Workspaces.AnyAsync(w => w.Slug == workspace.Slug, cancellationToken))
        {
            workspace.DisambiguateSlug(++suffix);
        }

        db.Workspaces.Add(workspace);
        db.Activity.Add(ActivityEntry.Create(workspace.Id, null, null, userId, $"created the workspace \"{workspace.Name}\"", workspace.CreatedAtUtc));
        await db.SaveChangesAsync(cancellationToken);

        return new WorkspaceSummary(workspace.Id, workspace.Name, workspace.Slug, WorkspaceRole.Owner, 1, 0);
    }

    public async Task<IReadOnlyList<WorkspaceSummary>> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(db.Workspaces, m => m.WorkspaceId, w => w.Id, (m, w) => new { Member = m, Workspace = w })
            .OrderBy(x => x.Workspace.Name)
            .Select(x => new WorkspaceSummary(
                x.Workspace.Id,
                x.Workspace.Name,
                x.Workspace.Slug,
                x.Member.Role,
                db.WorkspaceMembers.Count(m => m.WorkspaceId == x.Workspace.Id),
                db.Projects.Count(p => p.WorkspaceId == x.Workspace.Id && !p.IsArchived)))
            .ToListAsync(cancellationToken);
    }

    public async Task<Result<WorkspaceSummary>> GetBySlugAsync(string userId, string slug, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var summary = await db.Workspaces
            .AsNoTracking()
            .Where(w => w.Slug == slug)
            .Select(w => new
            {
                w.Id,
                w.Name,
                w.Slug,
                Role = w.Members.Where(m => m.UserId == userId).Select(m => (WorkspaceRole?)m.Role).FirstOrDefault(),
                MemberCount = w.Members.Count,
                ProjectCount = db.Projects.Count(p => p.WorkspaceId == w.Id && !p.IsArchived)
            })
            .SingleOrDefaultAsync(cancellationToken);

        // Non-members get "not found" rather than "forbidden" so workspace slugs can't be probed.
        if (summary?.Role is not { } role)
        {
            return WorkspaceErrors.NotFound;
        }

        return new WorkspaceSummary(summary.Id, summary.Name, summary.Slug, role, summary.MemberCount, summary.ProjectCount);
    }

    public async Task<Result<IReadOnlyList<MemberInfo>>> GetMembersAsync(string userId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (await db.GetMembershipAsync(workspaceId, userId, cancellationToken) is null)
        {
            return WorkspaceErrors.NotFound;
        }

        var members = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new MemberInfo(u.Id, u.DisplayName, u.Email!, m.Role, m.JoinedAtUtc))
            .ToListAsync(cancellationToken);

        // Roles are stored as strings, so rank them in memory (owner, admins, members).
        return members.OrderByDescending(m => m.Role).ThenBy(m => m.DisplayName).ToList();
    }

    public async Task<Result> AddMemberAsync(
        string actorId,
        Guid workspaceId,
        string email,
        WorkspaceRole role,
        CancellationToken cancellationToken = default)
    {
        if (role == WorkspaceRole.Owner)
        {
            return WorkspaceErrors.CannotAssignOwner;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var actor = await db.GetMembershipAsync(workspaceId, actorId, cancellationToken);
        if (actor is null)
        {
            return WorkspaceErrors.NotFound;
        }

        if (!actor.CanManageMembers)
        {
            return WorkspaceErrors.NotAllowed;
        }

        var normalizedEmail = email.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
        if (user is null)
        {
            return WorkspaceErrors.UserNotFound(email);
        }

        if (await db.GetMembershipAsync(workspaceId, user.Id, cancellationToken) is not null)
        {
            return WorkspaceErrors.AlreadyMember(email);
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        db.WorkspaceMembers.Add(WorkspaceMember.Create(workspaceId, user.Id, role, utcNow));
        db.Activity.Add(ActivityEntry.Create(workspaceId, null, null, actorId, $"added {user.DisplayName} as {role}", utcNow));
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result> RemoveMemberAsync(string actorId, Guid workspaceId, string memberUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var actor = await db.GetMembershipAsync(workspaceId, actorId, cancellationToken);
        if (actor is null)
        {
            return WorkspaceErrors.NotFound;
        }

        var member = await db.WorkspaceMembers.SingleOrDefaultAsync(
            m => m.WorkspaceId == workspaceId && m.UserId == memberUserId, cancellationToken);
        if (member is null)
        {
            return WorkspaceErrors.MemberNotFound;
        }

        // Members may leave on their own; removing someone else requires admin rights.
        var isSelf = actorId == memberUserId;
        if (!isSelf && !actor.CanManageMembers)
        {
            return WorkspaceErrors.NotAllowed;
        }

        if (member.Role == WorkspaceRole.Owner)
        {
            return WorkspaceErrors.CannotRemoveOwner;
        }

        db.WorkspaceMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public static class WorkspaceErrors
{
    public static readonly Error InvalidName = Error.Validation("Workspace.InvalidName", "Workspace name must be between 1 and 100 characters.");

    public static readonly Error NotFound = Error.NotFound("Workspace.NotFound", "Workspace not found.");

    public static readonly Error NotAllowed = Error.Forbidden("Workspace.NotAllowed", "Only owners and admins can manage members.");

    public static readonly Error CannotAssignOwner = Error.Validation("Workspace.CannotAssignOwner", "A workspace has exactly one owner.");

    public static readonly Error CannotRemoveOwner = Error.Conflict("Workspace.CannotRemoveOwner", "The workspace owner cannot be removed.");

    public static readonly Error MemberNotFound = Error.NotFound("Workspace.MemberNotFound", "That user is not a member of this workspace.");

    public static Error UserNotFound(string email) =>
        Error.NotFound("Workspace.UserNotFound", $"No TaskHive account uses the email '{email}'. Ask them to sign up first.");

    public static Error AlreadyMember(string email) =>
        Error.Conflict("Workspace.AlreadyMember", $"'{email}' is already a member of this workspace.");
}
