using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TaskHive.Core.Common;
using TaskHive.Core.Data;
using TaskHive.Core.Domain;
using TaskHive.Core.Workspaces;

namespace TaskHive.Core.Projects;

public sealed record ProjectSummary(Guid Id, string Name, string Key, string? Description, int OpenTasks, int DoneTasks);

public sealed record BoardView(
    Guid ProjectId,
    Guid WorkspaceId,
    string ProjectName,
    string ProjectKey,
    IReadOnlyList<ColumnView> Columns,
    IReadOnlyList<MemberOption> Members);

public sealed record ColumnView(Guid Id, string Name, int Position, IReadOnlyList<TaskCardView> Tasks);

public sealed record TaskCardView(
    Guid Id,
    string Key,
    string Title,
    string? Description,
    TaskPriority Priority,
    string? AssigneeId,
    string? AssigneeName,
    DateOnly? DueDate,
    Guid ColumnId,
    double Position,
    Guid Version);

public sealed record MemberOption(string UserId, string DisplayName);

public sealed partial class ProjectService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider timeProvider)
{
    public async Task<Result<ProjectSummary>> CreateAsync(
        string userId,
        Guid workspaceId,
        string name,
        string key,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
        {
            return ProjectErrors.InvalidName;
        }

        if (!KeyPattern().IsMatch(key.Trim()))
        {
            return ProjectErrors.InvalidKey;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (await db.GetMembershipAsync(workspaceId, userId, cancellationToken) is null)
        {
            return WorkspaceErrors.NotFound;
        }

        var normalizedKey = key.Trim().ToUpperInvariant();
        if (await db.Projects.AnyAsync(p => p.WorkspaceId == workspaceId && p.Key == normalizedKey, cancellationToken))
        {
            return ProjectErrors.KeyTaken(normalizedKey);
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var project = Project.Create(workspaceId, name, normalizedKey, description, utcNow);
        db.Projects.Add(project);
        db.Activity.Add(ActivityEntry.Create(workspaceId, project.Id, null, userId, $"created project {project.Key} \"{project.Name}\"", utcNow));
        await db.SaveChangesAsync(cancellationToken);

        return new ProjectSummary(project.Id, project.Name, project.Key, project.Description, 0, 0);
    }

    public async Task<Result<IReadOnlyList<ProjectSummary>>> GetForWorkspaceAsync(
        string userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (await db.GetMembershipAsync(workspaceId, userId, cancellationToken) is null)
        {
            return WorkspaceErrors.NotFound;
        }

        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && !p.IsArchived)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Key,
                p.Description,
                DoneColumnId = p.Columns.OrderByDescending(c => c.Position).Select(c => c.Id).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var projectIds = projects.Select(p => p.Id).ToList();
        var counts = await db.Tasks
            .AsNoTracking()
            .Where(t => projectIds.Contains(t.ProjectId))
            .GroupBy(t => new { t.ProjectId, t.ColumnId })
            .Select(g => new { g.Key.ProjectId, g.Key.ColumnId, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // The right-most column counts as "done".
        return projects
            .Select(p => new ProjectSummary(
                p.Id,
                p.Name,
                p.Key,
                p.Description,
                counts.Where(c => c.ProjectId == p.Id && c.ColumnId != p.DoneColumnId).Sum(c => c.Count),
                counts.Where(c => c.ProjectId == p.Id && c.ColumnId == p.DoneColumnId).Sum(c => c.Count)))
            .ToList();
    }

    public async Task<Result<BoardView>> GetBoardAsync(string userId, Guid workspaceId, string projectKey, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (await db.GetMembershipAsync(workspaceId, userId, cancellationToken) is null)
        {
            return WorkspaceErrors.NotFound;
        }

        var normalizedKey = projectKey.ToUpperInvariant();
        var project = await db.Projects
            .AsNoTracking()
            .Include(p => p.Columns)
            .SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.Key == normalizedKey, cancellationToken);

        if (project is null)
        {
            return ProjectErrors.NotFound;
        }

        var members = await db.WorkspaceMembers
            .AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u)
            .OrderBy(u => u.DisplayName)
            .Select(u => new MemberOption(u.Id, u.DisplayName))
            .ToListAsync(cancellationToken);

        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => t.ProjectId == project.Id)
            .OrderBy(t => t.Position)
            .ToListAsync(cancellationToken);

        var names = members.ToDictionary(m => m.UserId, m => m.DisplayName);

        var columns = project.Columns
            .OrderBy(c => c.Position)
            .Select(c => new ColumnView(
                c.Id,
                c.Name,
                c.Position,
                tasks.Where(t => t.ColumnId == c.Id).Select(t => ToCard(t, project.Key, names)).ToList()))
            .ToList();

        return new BoardView(project.Id, workspaceId, project.Name, project.Key, columns, members);
    }

    internal static TaskCardView ToCard(TaskItem task, string projectKey, IReadOnlyDictionary<string, string> names) => new(
        task.Id,
        $"{projectKey}-{task.Number}",
        task.Title,
        task.Description,
        task.Priority,
        task.AssigneeId,
        task.AssigneeId is not null && names.TryGetValue(task.AssigneeId, out var name) ? name : null,
        task.DueDate,
        task.ColumnId,
        task.Position,
        task.Version);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{1,9}$")]
    private static partial Regex KeyPattern();
}

public static class ProjectErrors
{
    public static readonly Error InvalidName = Error.Validation("Project.InvalidName", "Project name must be between 1 and 100 characters.");

    public static readonly Error InvalidKey = Error.Validation("Project.InvalidKey", "Key must be 2-10 letters or digits and start with a letter (e.g. WEB).");

    public static readonly Error NotFound = Error.NotFound("Project.NotFound", "Project not found.");

    public static Error KeyTaken(string key) => Error.Conflict("Project.KeyTaken", $"Another project in this workspace already uses the key {key}.");
}
