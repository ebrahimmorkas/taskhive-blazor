namespace TaskHive.Core.Domain;

public sealed class TaskItem
{
    private TaskItem()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ColumnId { get; private set; }

    public int Number { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public TaskPriority Priority { get; private set; }

    public string? AssigneeId { get; private set; }

    public DateOnly? DueDate { get; private set; }

    /// <summary>
    /// Sparse ordering key within a column. Moving a card sets it between its neighbours,
    /// so only the moved row is updated instead of renumbering the whole column.
    /// </summary>
    public double Position { get; private set; }

    public string CreatedById { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? UpdatedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token so two users editing the same card can't overwrite each other.</summary>
    public Guid Version { get; private set; }

    public static TaskItem Create(
        Project project,
        Guid columnId,
        string title,
        string? description,
        TaskPriority priority,
        string? assigneeId,
        DateOnly? dueDate,
        double position,
        string createdById,
        DateTime utcNow) => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = project.WorkspaceId,
        ProjectId = project.Id,
        ColumnId = columnId,
        Number = project.AllocateTaskNumber(),
        Title = title.Trim(),
        Description = description?.Trim(),
        Priority = priority,
        AssigneeId = assigneeId,
        DueDate = dueDate,
        Position = position,
        CreatedById = createdById,
        CreatedAtUtc = utcNow,
        Version = Guid.NewGuid()
    };

    public void Update(string title, string? description, TaskPriority priority, string? assigneeId, DateOnly? dueDate, DateTime utcNow)
    {
        Title = title.Trim();
        Description = description?.Trim();
        Priority = priority;
        AssigneeId = assigneeId;
        DueDate = dueDate;
        Touch(utcNow);
    }

    public void MoveTo(Guid columnId, double position, DateTime utcNow)
    {
        ColumnId = columnId;
        Position = position;
        Touch(utcNow);
    }

    public bool IsOverdue(DateOnly today) => DueDate is { } due && due < today;

    private void Touch(DateTime utcNow)
    {
        UpdatedAtUtc = utcNow;
        Version = Guid.NewGuid();
    }
}

public enum TaskPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Urgent = 3
}

public sealed class TaskComment
{
    private TaskComment()
    {
    }

    public Guid Id { get; private set; }

    public Guid TaskId { get; private set; }

    public string AuthorId { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public static TaskComment Create(Guid taskId, string authorId, string body, DateTime utcNow) => new()
    {
        Id = Guid.CreateVersion7(),
        TaskId = taskId,
        AuthorId = authorId,
        Body = body.Trim(),
        CreatedAtUtc = utcNow
    };
}

/// <summary>Audit trail of what happened in a workspace, shown as an activity feed.</summary>
public sealed class ActivityEntry
{
    private ActivityEntry()
    {
    }

    public long Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid? ProjectId { get; private set; }

    public Guid? TaskId { get; private set; }

    public string ActorId { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public static ActivityEntry Create(Guid workspaceId, Guid? projectId, Guid? taskId, string actorId, string message, DateTime utcNow) => new()
    {
        WorkspaceId = workspaceId,
        ProjectId = projectId,
        TaskId = taskId,
        ActorId = actorId,
        Message = message,
        CreatedAtUtc = utcNow
    };
}
