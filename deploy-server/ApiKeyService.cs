using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace DeployKit.DeployServer;

public class ApiKeyService
{
    private readonly DbConnectionFactory _db;

    public ApiKeyService(DbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<ApiKeyCreatedResponse> CreateAsync(string name, string[] permissions)
    {
        var rawKey = "dk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var keyHash = HashKey(rawKey);
        var keyPrefix = rawKey[..Math.Min(11, rawKey.Length)]; // "dk_" + 8 chars
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var permissionsStr = string.Join(",", permissions);

        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO api_keys (id, name, key_hash, key_prefix, permissions, created_at, revoked)
            VALUES (@id, @name, @keyHash, @keyPrefix, @permissions, @createdAt, 0)
            """,
            new
            {
                id,
                name,
                keyHash,
                keyPrefix,
                permissions = permissionsStr,
                createdAt = now.ToString("O")
            });

        return new ApiKeyCreatedResponse(id, name, rawKey, keyPrefix, permissions, now);
    }

    public async Task<bool> RevokeAsync(string id)
    {
        await using var conn = _db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            "UPDATE api_keys SET revoked = 1 WHERE id = @id AND revoked = 0",
            new { id });
        return affected > 0;
    }

    public async Task<List<ApiKeyListItem>> ListAsync()
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync(
            "SELECT id, name, key_prefix, permissions, created_at, revoked FROM api_keys ORDER BY created_at DESC");

        return rows.Select(row => new ApiKeyListItem(
            Id: (string)row.id,
            Name: (string)row.name,
            KeyPrefix: (string)row.key_prefix,
            Permissions: ((string)row.permissions).Split(',', StringSplitOptions.RemoveEmptyEntries),
            CreatedAt: DateTime.Parse((string)row.created_at, null, System.Globalization.DateTimeStyles.RoundtripKind),
            Revoked: (long)row.revoked == 1
        )).ToList();
    }

    public async Task<ApiKeyRecord?> ValidateAsync(string rawKey)
    {
        var keyHash = HashKey(rawKey);

        await using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync(
            "SELECT * FROM api_keys WHERE key_hash = @keyHash AND revoked = 0",
            new { keyHash });

        if (row is null) return null;

        return new ApiKeyRecord(
            Id: (string)row.id,
            Name: (string)row.name,
            KeyHash: (string)row.key_hash,
            KeyPrefix: (string)row.key_prefix,
            Permissions: ((string)row.permissions).Split(',', StringSplitOptions.RemoveEmptyEntries),
            CreatedAt: DateTime.Parse((string)row.created_at, null, System.Globalization.DateTimeStyles.RoundtripKind),
            Revoked: false);
    }

    private static string HashKey(string rawKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
}
