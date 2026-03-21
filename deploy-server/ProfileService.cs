using System.Text.Json;
using StackExchange.Redis;

namespace DeployKit.DeployServer;

public class ProfileService
{
    private readonly IDatabase _redis;
    private readonly ILogger<ProfileService> _logger;

    private static string ProfileKey(string id) => $"deploy:profile:{id}";
    private const string ProfilesIndex = "deploy:profiles";

    public ProfileService(IConnectionMultiplexer redis, ILogger<ProfileService> logger)
    {
        _redis = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<BuildProfile> CreateProfileAsync(string name, string workingDirectory, EnvVar[] envVars, string[] steps)
    {
        var id = Guid.NewGuid().ToString("N");
        var profile = new BuildProfile(id, name, workingDirectory, envVars, steps);
        await _redis.StringSetAsync(ProfileKey(id), JsonSerializer.Serialize(profile));
        await _redis.SetAddAsync(ProfilesIndex, id);
        _logger.LogInformation("Created profile {Id} name={Name}", id, name);
        return profile;
    }

    public async Task<BuildProfile?> GetProfileAsync(string id)
    {
        var json = await _redis.StringGetAsync(ProfileKey(id));
        if (json.IsNullOrEmpty) return null;
        return JsonSerializer.Deserialize<BuildProfile>(json.ToString());
    }

    public async Task<List<BuildProfile>> ListProfilesAsync()
    {
        var ids = await _redis.SetMembersAsync(ProfilesIndex);
        var profiles = new List<BuildProfile>();
        foreach (var id in ids)
        {
            var profile = await GetProfileAsync(id.ToString());
            if (profile is not null) profiles.Add(profile);
        }
        return profiles.OrderBy(p => p.Name).ToList();
    }

    public async Task<bool> DeleteProfileAsync(string id)
    {
        var exists = await _redis.KeyExistsAsync(ProfileKey(id));
        if (!exists) return false;
        await _redis.KeyDeleteAsync(ProfileKey(id));
        await _redis.SetRemoveAsync(ProfilesIndex, id);
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
        await _redis.StringSetAsync(ProfileKey(id), JsonSerializer.Serialize(updated));
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
        var ids = await _redis.SetMembersAsync(ProfilesIndex);
        foreach (var id in ids)
        {
            if (id.ToString() == excludeId) continue;
            var profile = await GetProfileAsync(id.ToString());
            if (profile is not null && string.Equals(profile.Name, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
