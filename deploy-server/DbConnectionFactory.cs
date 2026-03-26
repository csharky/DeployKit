using Dapper;
using Microsoft.Data.Sqlite;

namespace DeployKit.DeployServer;

public class DbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        var path = configuration["Database:Path"] ?? "deploy.db";
        _connectionString = $"Data Source={path}";
    }

    public SqliteConnection CreateConnection() => new(_connectionString);

    public async Task InitializeAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();

        await conn.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS profiles (
                id                TEXT PRIMARY KEY,
                name              TEXT NOT NULL UNIQUE,
                working_directory TEXT NOT NULL DEFAULT '',
                steps             TEXT NOT NULL DEFAULT '[]',
                env_vars          TEXT NOT NULL DEFAULT '[]',
                created_at        TEXT NOT NULL,
                updated_at        TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS jobs (
                id                TEXT PRIMARY KEY,
                profile_id        TEXT NOT NULL,
                profile_snapshot  TEXT NOT NULL,
                status            TEXT NOT NULL DEFAULT 'pending',
                env_overrides     TEXT,
                created_at        TEXT NOT NULL,
                started_at        TEXT,
                completed_at      TEXT,
                logs              TEXT NOT NULL DEFAULT '',
                error             TEXT,
                artifact_path     TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_jobs_status ON jobs(status);
            CREATE INDEX IF NOT EXISTS idx_jobs_created ON jobs(created_at DESC);

            CREATE TABLE IF NOT EXISTS agents (
                id        TEXT PRIMARY KEY,
                last_seen TEXT,
                logs      TEXT NOT NULL DEFAULT '[]'
            );

            CREATE TABLE IF NOT EXISTS api_keys (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                key_hash    TEXT NOT NULL UNIQUE,
                key_prefix  TEXT NOT NULL,
                permissions TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                revoked     INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS audit_log (
                id        TEXT PRIMARY KEY,
                key_name  TEXT NOT NULL,
                action    TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                details   TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_audit_timestamp ON audit_log(timestamp DESC);
            """);
    }
}
