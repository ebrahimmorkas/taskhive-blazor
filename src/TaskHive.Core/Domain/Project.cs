namespace TaskHive.Core.Domain;

public sealed class Project
{
    public static readonly string[] DefaultColumns = ["To do", "In progress", "In review", "Done"];

    private readonly List<BoardColumn> _columns = [];

    private Project()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Short uppercase prefix used for task keys, e.g. <c>WEB</c> → <c>WEB-42</c>.</summary>
    public string Key { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsArchived { get; private set; }

    public int NextTaskNumber { get; private set; } = 1;

    public DateTime CreatedAtUtc { get; private set; }

    public IReadOnlyCollection<BoardColumn> Columns => _columns.AsReadOnly();

    public static Project Create(Guid workspaceId, string name, string key, string? description, DateTime utcNow)
    {
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            Name = name.Trim(),
            Key = key.Trim().ToUpperInvariant(),
            Description = description?.Trim(),
            CreatedAtUtc = utcNow
        };

        for (var i = 0; i < DefaultColumns.Length; i++)
        {
            project._columns.Add(new BoardColumn(Guid.CreateVersion7(), project.Id, DefaultColumns[i], i));
        }

        return project;
    }

    public int AllocateTaskNumber() => NextTaskNumber++;

    public void Archive() => IsArchived = true;
}

public sealed class BoardColumn
{
    internal BoardColumn(Guid id, Guid projectId, string name, int position)
    {
        Id = id;
        ProjectId = projectId;
        Name = name;
        Position = position;
    }

    private BoardColumn()
    {
    }

    public Guid Id { get; private set; }

    public Guid ProjectId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int Position { get; private set; }
}
