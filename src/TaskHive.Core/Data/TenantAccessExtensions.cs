using Microsoft.EntityFrameworkCore;
using TaskHive.Core.Domain;

namespace TaskHive.Core.Data;

internal static class TenantAccessExtensions
{
    /// <summary>Returns the caller's membership in a workspace, or <c>null</c> if they have no access.</summary>
    public static Task<WorkspaceMember?> GetMembershipAsync(
        this ApplicationDbContext db,
        Guid workspaceId,
        string userId,
        CancellationToken cancellationToken) =>
        db.WorkspaceMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, cancellationToken);
}
