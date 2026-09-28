using Microsoft.EntityFrameworkCore;
using TaskHive.Core.Common;
using TaskHive.Core.Data;
using TaskHive.Core.Domain;
using TaskHive.Core.Tasks;
using TaskHive.Core.Workspaces;

namespace TaskHive.Core.Collaboration;

public sealed record CommentView(Guid Id, string AuthorId, string AuthorName, string Body, DateTime CreatedAtUtc);

public sealed record ActivityView(long Id, string ActorName, string Message, DateTime CreatedAtUtc);

public sealed record MyTaskView(
    Guid TaskId,
    string Key,
    string Title,
    TaskPriority Priority,
    DateOnly? DueDate,
    string ColumnName,
    string ProjectName,
    string WorkspaceSlug,
    string ProjectKey,
    bool IsOverdue);

/// <summary>
/// Comments, the workspace activity feed and the personal "my tasks" dashboard.
/// </summary>
public sealed class CollaborationService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider timeProvider)
{
    public const int MaxCommentLength = 4000;

    public async Task<Result<CommentView>> AddCommentAsync(string userId, Guid taskId, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxCommentLength)
        {
            return CollaborationErrors.InvalidComment;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var task = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null || await db.GetMembershipAsync(task.WorkspaceId, userId, cancellationToken) is null)
        {
            return TaskErrors.NotFound;
        }

        var projectKey = await db.Projects.Where(p => p.Id == task.ProjectId).Select(p => p.Key).SingleAsync(cancellationToken);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var comment = TaskComment.Create(taskId, userId, body, utcNow);

        db.Comments.Add(comment);
        db.Activity.Add(ActivityEntry.Create(task.WorkspaceId, task.ProjectId, task.Id, userId, $"commented on {projectKey}-{task.Number}", utcNow));
        await db.SaveChangesAsync(cancellationToken);

        var authorName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(cancellationToken);
        return new CommentView(comment.Id, userId, authorName, comment.Body, comment.CreatedAtUtc);
    }

    public async Task<Result<IReadOnlyList<CommentView>>> GetCommentsAsync(string userId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var workspaceId = await db.Tasks.Where(t => t.Id == taskId).Select(t => (Guid?)t.WorkspaceId).SingleOrDefaultAsync(cancellationToken);
        if (workspaceId is null || await db.GetMembershipAsync(workspaceId.Value, userId, cancellationToken) is null)
        {
            return TaskErrors.NotFound;
        }

        var comments = await db.Comments
            .AsNoTracking()
            .Where(c => c.TaskId == taskId)
            .OrderBy(c => c.CreatedAtUtc)
            .Join(db.Users, c => c.AuthorId, u => u.Id, (c, u) => new CommentView(c.Id, u.Id, u.DisplayName, c.Body, c.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return comments;
    }

    public async Task<Result<IReadOnlyList<ActivityView>>> GetActivityAsync(
        string userId,
        Guid workspaceId,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (await db.GetMembershipAsync(workspaceId, userId, cancellationToken) is null)
        {
            return WorkspaceErrors.NotFound;
        }

        var entries = await db.Activity
            .AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId)
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(take, 1, 200))
            .Join(db.Users, a => a.ActorId, u => u.Id, (a, u) => new ActivityView(a.Id, u.DisplayName, a.Message, a.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return entries;
    }

    /// <summary>
    /// Open tasks assigned to the user across all their workspaces (the right-most column counts as done),
    /// overdue first, then by due date and priority.
    /// </summary>
    public async Task<IReadOnlyList<MyTaskView>> GetMyTasksAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var workspaceIds = db.WorkspaceMembers.Where(m => m.UserId == userId).Select(m => m.WorkspaceId);

        var rows = await (
            from task in db.Tasks.AsNoTracking()
            where task.AssigneeId == userId && workspaceIds.Contains(task.WorkspaceId)
            join project in db.Projects on task.ProjectId equals project.Id
            join workspace in db.Workspaces on task.WorkspaceId equals workspace.Id
            join column in db.BoardColumns on task.ColumnId equals column.Id
            where column.Position < db.BoardColumns.Where(c => c.ProjectId == project.Id).Max(c => c.Position)
            select new
            {
                task.Id,
                task.Number,
                task.Title,
                task.Priority,
                task.DueDate,
                ColumnName = column.Name,
                ProjectName = project.Name,
                ProjectKey = project.Key,
                WorkspaceSlug = workspace.Slug
            })
            .ToListAsync(cancellationToken);

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        return rows
            .Select(r => new MyTaskView(
                r.Id, $"{r.ProjectKey}-{r.Number}", r.Title, r.Priority, r.DueDate, r.ColumnName, r.ProjectName,
                r.WorkspaceSlug, r.ProjectKey, r.DueDate is { } due && due < today))
            .OrderByDescending(t => t.IsOverdue)
            .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
            .ThenByDescending(t => t.Priority)
            .ToList();
    }
}

public static class CollaborationErrors
{
    public static readonly Error InvalidComment =
        Error.Validation("Comment.Invalid", $"Comments must be between 1 and {CollaborationService.MaxCommentLength} characters.");
}
