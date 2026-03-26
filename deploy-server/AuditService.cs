using Dapper;

namespace DeployKit.DeployServer;

public class AuditService
{
    private readonly DbConnectionFactory _db;
    private readonly int _maxEntries;

    public AuditService(DbConnectionFactory db, IConfiguration configuration)
    {
        _db = db;
        _maxEntries = configuration.GetValue<int>("Audit:MaxEntries", 500);
    }

    public async Task LogAsync(string keyName, string action, string? details = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO audit_log (id, key_name, action, timestamp, details)
            VALUES (@id, @keyName, @action, @timestamp, @details)
            """,
            new { id, keyName, action, timestamp = now.ToString("O"), details });

        // Trim old entries beyond max
        await conn.ExecuteAsync("""
            DELETE FROM audit_log WHERE id IN (
                SELECT id FROM audit_log ORDER BY timestamp DESC LIMIT -1 OFFSET @maxEntries
            )
            """,
            new { maxEntries = _maxEntries });
    }

    public async Task<List<AuditEntry>> GetRecentAsync(int count = 100)
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync(
            "SELECT * FROM audit_log ORDER BY timestamp DESC LIMIT @count",
            new { count });

        return rows.Select(row => new AuditEntry(
            Id: (string)row.id,
            KeyName: (string)row.key_name,
            Action: (string)row.action,
            Timestamp: DateTime.Parse((string)row.timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind),
            Details: (string?)row.details
        )).ToList();
    }
}
