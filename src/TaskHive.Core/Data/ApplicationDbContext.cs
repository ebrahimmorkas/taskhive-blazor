using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TaskHive.Core.Domain;

namespace TaskHive.Core.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<BoardColumn> BoardColumns => Set<BoardColumn>();

    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    public DbSet<TaskComment> Comments => Set<TaskComment>();

    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(u => u.Property(x => x.DisplayName).HasMaxLength(100));

        builder.Entity<Workspace>(w =>
        {
            w.HasKey(x => x.Id);
            w.Property(x => x.Name).HasMaxLength(100).IsRequired();
            w.Property(x => x.Slug).HasMaxLength(60).IsRequired();
            w.HasIndex(x => x.Slug).IsUnique();
            w.HasMany(x => x.Members).WithOne().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            w.Navigation(x => x.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<WorkspaceMember>(m =>
        {
            m.HasKey(x => new { x.WorkspaceId, x.UserId });
            m.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            m.Ignore(x => x.CanManageMembers);
            m.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            m.HasIndex(x => x.UserId);
        });

        builder.Entity<Project>(p =>
        {
            p.HasKey(x => x.Id);
            p.Property(x => x.Name).HasMaxLength(100).IsRequired();
            p.Property(x => x.Key).HasMaxLength(10).IsRequired();
            p.Property(x => x.Description).HasMaxLength(1000);
            p.Property(x => x.NextTaskNumber).IsConcurrencyToken();
            p.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            p.HasIndex(x => new { x.WorkspaceId, x.Key }).IsUnique();
            p.HasMany(x => x.Columns).WithOne().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
            p.Navigation(x => x.Columns).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<BoardColumn>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.Name).HasMaxLength(50).IsRequired();
        });

        builder.Entity<TaskItem>(t =>
        {
            t.HasKey(x => x.Id);
            t.Property(x => x.Title).HasMaxLength(200).IsRequired();
            t.Property(x => x.Description).HasMaxLength(4000);
            t.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
            t.Property(x => x.Version).IsConcurrencyToken();
            t.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            t.HasOne<BoardColumn>().WithMany().HasForeignKey(x => x.ColumnId).OnDelete(DeleteBehavior.Restrict);
            t.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.SetNull);
            t.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique();
            t.HasIndex(x => new { x.WorkspaceId, x.AssigneeId });
        });

        builder.Entity<TaskComment>(c =>
        {
            c.HasKey(x => x.Id);
            c.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            c.HasOne<TaskItem>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            c.HasIndex(x => x.TaskId);
        });

        builder.Entity<ActivityEntry>(a =>
        {
            a.HasKey(x => x.Id);
            a.Property(x => x.Message).HasMaxLength(500).IsRequired();
            a.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc });
        });
    }
}
