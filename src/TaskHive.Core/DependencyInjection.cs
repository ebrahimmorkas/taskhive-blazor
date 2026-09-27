using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskHive.Core.Data;
using TaskHive.Core.Workspaces;

namespace TaskHive.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddTaskHiveCore(this IServiceCollection services, string connectionString)
    {
        // Blazor Server keeps one DI scope per circuit (browser tab), so a scoped DbContext would be
        // shared by concurrent UI events. Services create a short-lived context per operation instead.
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<WorkspaceService>();

        return services;
    }
}
