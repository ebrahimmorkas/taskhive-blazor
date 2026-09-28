using Microsoft.EntityFrameworkCore;
using TaskHive.Core.Common;
using TaskHive.Core.Data;
using TaskHive.Core.Domain;
using TaskHive.Core.Projects;

namespace TaskHive.Core.Tasks;

public sealed record TaskInput(
    string Title,
    string? Description,
    TaskPriority Priority,
    string? AssigneeId,
    DateOnly? DueDate);

public sealed class TaskService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider timeProvider)
{
    /// <summary>Gap between neighbouring cards; leaves room for many inserts before positions get close.</summary>
    internal const double PositionStep = 1024;

    public async Task<Result<TaskCardView>> CreateAsync(
        string userId,
        Guid projectId,
        Guid columnId,
        TaskInput input,
        CancellationToken cancellationToken = default)
    {
        if (Validate(input) is { } invalid)
        {
            return invalid;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var project = await db.Projects.Include(p => p.Columns).SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project is null || await db.GetMembershipAsync(project.WorkspaceId, userId, cancellationToken) is null)
        {
            return ProjectErrors.NotFound;
        }

        if (project.Columns.All(c => c.Id != columnId))
        {
            return TaskErrors.ColumnNotFound;
        }

        if (input.AssigneeId is not null && await db.GetMembershipAsync(project.WorkspaceId, input.AssigneeId, cancellationToken) is null)
        {
            return TaskErrors.AssigneeNotMember;
        }

        var lastPosition = await db.Tasks
            .Where(t => t.ColumnId == columnId)
            .MaxAsync(t => (double?)t.Position, cancellationToken) ?? 0;

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var task = TaskItem.Create(
            project, columnId, input.Title, input.Description, input.Priority, input.AssigneeId, input.DueDate,
            lastPosition + PositionStep, userId, utcNow);

        db.Tasks.Add(task);
        db.Activity.Add(ActivityEntry.Create(project.WorkspaceId, project.Id, task.Id, userId, $"created {project.Key}-{task.Number} \"{task.Title}\"", utcNow));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two tasks were created in the same project at the same moment and raced for the next number.
            return TaskErrors.Conflict;
        }

        return ProjectService.ToCard(task, project.Key, await GetNamesAsync(db, task.AssigneeId, cancellationToken));
    }

    public async Task<Result<TaskCardView>> UpdateAsync(
        string userId,
        Guid taskId,
        Guid expectedVersion,
        TaskInput input,
        CancellationToken cancellationToken = default)
    {
        if (Validate(input) is { } invalid)
        {
            return invalid;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var (task, project, error) = await LoadForMemberAsync(db, userId, taskId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (task!.Version != expectedVersion)
        {
            return TaskErrors.Conflict;
        }

        if (input.AssigneeId is not null && await db.GetMembershipAsync(project!.WorkspaceId, input.AssigneeId, cancellationToken) is null)
        {
            return TaskErrors.AssigneeNotMember;
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        task.Update(input.Title, input.Description, input.Priority, input.AssigneeId, input.DueDate, utcNow);
        db.Activity.Add(ActivityEntry.Create(project!.WorkspaceId, project.Id, task.Id, userId, $"updated {project.Key}-{task.Number}", utcNow));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TaskErrors.Conflict;
        }

        return ProjectService.ToCard(task, project.Key, await GetNamesAsync(db, task.AssigneeId, cancellationToken));
    }

    /// <summary>
    /// Moves a card to <paramref name="targetIndex"/> in <paramref name="targetColumnId"/>. Only the moved
    /// row changes: its position becomes the midpoint of its new neighbours.
    /// </summary>
    public async Task<Result<TaskCardView>> MoveAsync(
        string userId,
        Guid taskId,
        Guid targetColumnId,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var (task, project, error) = await LoadForMemberAsync(db, userId, taskId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var targetColumn = project!.Columns.SingleOrDefault(c => c.Id == targetColumnId);
        if (targetColumn is null)
        {
            return TaskErrors.ColumnNotFound;
        }

        var siblings = await db.Tasks
            .Where(t => t.ColumnId == targetColumnId && t.Id != taskId)
            .OrderBy(t => t.Position)
            .Select(t => t.Position)
            .ToListAsync(cancellationToken);

        var position = CalculatePosition(siblings, targetIndex);
        var movedColumn = task!.ColumnId != targetColumnId;
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        task.MoveTo(targetColumnId, position, utcNow);
        if (movedColumn)
        {
            db.Activity.Add(ActivityEntry.Create(project.WorkspaceId, project.Id, task.Id, userId, $"moved {project.Key}-{task.Number} to {targetColumn.Name}", utcNow));
        }

        await db.SaveChangesAsync(cancellationToken);

        return ProjectService.ToCard(task, project.Key, await GetNamesAsync(db, task.AssigneeId, cancellationToken));
    }

    public async Task<Result> DeleteAsync(string userId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var (task, project, error) = await LoadForMemberAsync(db, userId, taskId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        db.Tasks.Remove(task!);
        db.Activity.Add(ActivityEntry.Create(project!.WorkspaceId, project.Id, null, userId, $"deleted {project.Key}-{task!.Number} \"{task.Title}\"", timeProvider.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    internal static double CalculatePosition(IReadOnlyList<double> orderedSiblingPositions, int targetIndex)
    {
        var count = orderedSiblingPositions.Count;
        var index = Math.Clamp(targetIndex, 0, count);

        if (count == 0)
        {
            return PositionStep;
        }

        if (index == 0)
        {
            return orderedSiblingPositions[0] - PositionStep;
        }

        if (index == count)
        {
            return orderedSiblingPositions[^1] + PositionStep;
        }

        return (orderedSiblingPositions[index - 1] + orderedSiblingPositions[index]) / 2;
    }

    private static Error? Validate(TaskInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 200)
        {
            return TaskErrors.InvalidTitle;
        }

        return input.Description?.Length > 4000 ? TaskErrors.DescriptionTooLong : null;
    }

    private static async Task<(TaskItem? Task, Project? Project, Error? Error)> LoadForMemberAsync(
        ApplicationDbContext db,
        string userId,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        var task = await db.Tasks.SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        if (task is null || await db.GetMembershipAsync(task.WorkspaceId, userId, cancellationToken) is null)
        {
            return (null, null, TaskErrors.NotFound);
        }

        var project = await db.Projects.Include(p => p.Columns).SingleAsync(p => p.Id == task.ProjectId, cancellationToken);
        return (task, project, null);
    }

    private static async Task<IReadOnlyDictionary<string, string>> GetNamesAsync(
        ApplicationDbContext db,
        string? userId,
        CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return new Dictionary<string, string>();
        }

        return await db.Users.Where(u => u.Id == userId).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
    }
}

public static class TaskErrors
{
    public static readonly Error InvalidTitle = Error.Validation("Task.InvalidTitle", "Title must be between 1 and 200 characters.");

    public static readonly Error DescriptionTooLong = Error.Validation("Task.DescriptionTooLong", "Description can be at most 4000 characters.");

    public static readonly Error NotFound = Error.NotFound("Task.NotFound", "Task not found.");

    public static readonly Error ColumnNotFound = Error.NotFound("Task.ColumnNotFound", "That column doesn't belong to this project.");

    public static readonly Error AssigneeNotMember = Error.Validation("Task.AssigneeNotMember", "Tasks can only be assigned to workspace members.");

    public static readonly Error Conflict = Error.Conflict("Task.Conflict", "Someone else changed this task. Reload to see the latest version.");
}
