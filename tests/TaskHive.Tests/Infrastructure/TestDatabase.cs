using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskHive.Core.Data;

namespace TaskHive.Tests.Infrastructure;

/// <summary>
/// Real relational database (SQLite in-memory) per test, so queries, constraints and cascades behave
/// like production instead of relying on the EF in-memory provider.
/// </summary>
public sealed class TestDatabase : IDbContextFactory<ApplicationDbContext>, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    private TestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
    }

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        await using var db = database.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return database;
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    public async Task<ApplicationUser> AddUserAsync(string displayName, string? email = null)
    {
        email ??= $"{displayName.ToLowerInvariant().Replace(' ', '.')}@example.com";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = displayName
        };

        await using var db = CreateDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
