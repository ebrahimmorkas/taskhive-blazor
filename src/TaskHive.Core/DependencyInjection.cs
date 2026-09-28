using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskHive.Core.Data;
using TaskHive.Core.Projects;
using TaskHive.Core.Realtime;
using TaskHive.Core.Tasks;
using TaskHive.Core.Workspaces;

namespace TaskHive.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddTaskHiveCore(this IServiceCollection services, string connectionString)
    {
        EnsureDatabaseDirectoryExists(connectionString);

        // Blazor Server keeps one DI scope per circuit (browser tab), so a scoped DbContext would be
        // shared by concurrent UI events. Services create a short-lived context per operation instead.
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<WorkspaceService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<TaskService>();
        services.AddSingleton<IBoardNotifier, InMemoryBoardNotifier>();
        services.AddSingleton<PresenceTracker>();

        return services;
    }

    private static void EnsureDatabaseDirectoryExists(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory) && dataSource != ":memory:")
        {
            Directory.CreateDirectory(directory);
        }
    }
}
