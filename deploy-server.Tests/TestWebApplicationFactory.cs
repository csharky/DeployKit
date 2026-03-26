using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace DeployKit.DeployServer.Tests;

/// <summary>
/// Custom factory that uses a temporary SQLite file per test class instance,
/// ensuring test isolation without requiring Redis.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dbPath = Path.GetTempFileName() + ".db";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath
            });
        });
    }

    /// <summary>
    /// Deletes all test data from all tables between tests.
    /// Faster than recreating the factory.
    /// </summary>
    public async Task ClearAllDataAsync()
    {
        var connStr = $"Data Source={_dbPath}";
        await using var conn = new SqliteConnection(connStr);
        await conn.ExecuteAsync("""
            DELETE FROM jobs;
            DELETE FROM profiles;
            DELETE FROM agents;
            DELETE FROM api_keys;
            DELETE FROM audit_log;
            """);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
