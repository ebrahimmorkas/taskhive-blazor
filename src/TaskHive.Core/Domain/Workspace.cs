using System.Text.RegularExpressions;

namespace TaskHive.Core.Domain;

/// <summary>
/// The tenant. Every project, task and activity entry belongs to exactly one workspace, and users
/// only see data from workspaces they are members of.
/// </summary>
public sealed partial class Workspace
{
    private readonly List<WorkspaceMember> _members = [];

    private Workspace()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public IReadOnlyCollection<WorkspaceMember> Members => _members.AsReadOnly();

    public static Workspace Create(string name, string ownerId, DateTime utcNow)
    {
        var workspace = new Workspace
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Slug = ToSlug(name),
            CreatedAtUtc = utcNow
        };

        workspace._members.Add(new WorkspaceMember(workspace.Id, ownerId, WorkspaceRole.Owner, utcNow));
        return workspace;
    }

    public void Rename(string name) => Name = name.Trim();

    /// <summary>Appends a suffix so the slug stays unique across tenants.</summary>
    public void DisambiguateSlug(int suffix) => Slug = $"{ToSlug(Name)}-{suffix}";

    public static string ToSlug(string value)
    {
        var slug = NonAlphanumeric().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "workspace" : slug[..Math.Min(slug.Length, 50)];
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}

public sealed class WorkspaceMember
{
    internal WorkspaceMember(Guid workspaceId, string userId, WorkspaceRole role, DateTime joinedAtUtc)
    {
        WorkspaceId = workspaceId;
        UserId = userId;
        Role = role;
        JoinedAtUtc = joinedAtUtc;
    }

    private WorkspaceMember()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    public WorkspaceRole Role { get; private set; }

    public DateTime JoinedAtUtc { get; private set; }

    public bool CanManageMembers => Role is WorkspaceRole.Owner or WorkspaceRole.Admin;

    public void ChangeRole(WorkspaceRole role) => Role = role;

    public static WorkspaceMember Create(Guid workspaceId, string userId, WorkspaceRole role, DateTime utcNow) =>
        new(workspaceId, userId, role, utcNow);
}

public enum WorkspaceRole
{
    Member = 0,
    Admin = 1,
    Owner = 2
}
