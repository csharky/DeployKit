using System.Text.Json;
using Dapper;

namespace DeployKit.DeployServer;

public class ProfileService
{
    private readonly DbConnectionFactory _db;
    private readonly ILogger<ProfileService> _logger;

    public ProfileService(DbConnectionFactory db, ILogger<ProfileService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<BuildProfile> CreateProfileAsync(string name, string workingDirectory, EnvVar[] envVars, string[] steps)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");
        var profile = new BuildProfile(id, name, workingDirectory, envVars, steps);

        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO profiles (id, name, working_directory, steps, env_vars, created_at, updated_at)
            VALUES (@id, @name, @workingDirectory, @steps, @envVars, @now, @now)
            """,
            new
            {
                id,
                name,
                workingDirectory,
                steps = JsonSerializer.Serialize(steps),
                envVars = JsonSerializer.Serialize(envVars),
                now
            });

        _logger.LogInformation("Created profile {Id} name={Name}", id, name);
        return profile;
    }

    public async Task<BuildProfile?> GetProfileAsync(string id)
    {
        await using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync(
            "SELECT * FROM profiles WHERE id = @id", new { id });
        return row is null ? null : MapRow(row);
    }

    public async Task<List<BuildProfile>> ListProfilesAsync()
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync("SELECT * FROM profiles ORDER BY name");
        return rows.Select(MapRow).ToList();
    }

    public async Task<bool> DeleteProfileAsync(string id)
    {
        await using var conn = _db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            "DELETE FROM profiles WHERE id = @id", new { id });
        if (affected == 0) return false;
        _logger.LogInformation("Deleted profile {Id}", id);
        return true;
    }

    public async Task<BuildProfile?> UpdateProfileAsync(string id, UpdateProfileRequest request)
    {
        var stored = await GetProfileAsync(id);
        if (stored is null) return null;

        // Sentinel merge: preserve stored secret values when incoming value is "***"
        var existingByKey = stored.EnvVars.ToDictionary(v => v.Key);
        var mergedVars = request.EnvVars.Select(v =>
        {
            if (v.IsSecret && v.Value == "***" && existingByKey.TryGetValue(v.Key, out var existing))
                return v with { Value = existing.Value };
            return v;
        }).ToArray();

        var updated = new BuildProfile(id, request.Name, request.WorkingDirectory, mergedVars, request.Steps);

        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            UPDATE profiles SET
                name = @name,
                working_directory = @workingDirectory,
                steps = @steps,
                env_vars = @envVars,
                updated_at = @now
            WHERE id = @id
            """,
            new
            {
                id,
                name = request.Name,
                workingDirectory = request.WorkingDirectory,
                steps = JsonSerializer.Serialize(request.Steps),
                envVars = JsonSerializer.Serialize(mergedVars),
                now = DateTime.UtcNow.ToString("O")
            });

        _logger.LogInformation("Updated profile {Id} name={Name}", id, request.Name);
        return updated;
    }

    public static ProfileResponse MaskSecrets(BuildProfile profile)
    {
        var maskedVars = profile.EnvVars.Select(v =>
            v.IsSecret ? v with { Value = "***" } : v).ToArray();
        return new ProfileResponse(profile.Id, profile.Name, profile.WorkingDirectory, maskedVars, profile.Steps);
    }

    public async Task<bool> IsNameTakenAsync(string name, string? excludeId = null)
    {
        await using var conn = _db.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM profiles WHERE name = @name AND (@excludeId IS NULL OR id != @excludeId)",
            new { name, excludeId });
        return count > 0;
    }

    private static BuildProfile MapRow(dynamic row)
    {
        var envVars = JsonSerializer.Deserialize<EnvVar[]>((string)row.env_vars) ?? [];
        var steps = JsonSerializer.Deserialize<string[]>((string)row.steps) ?? [];
        return new BuildProfile(
            (string)row.id,
            (string)row.name,
            (string)row.working_directory,
            envVars,
            steps);
    }
}
