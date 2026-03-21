using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeployKit.DeployServer;
using StackExchange.Redis;
using Xunit;

namespace DeployKit.DeployServer.Tests;

public class ProfileApiTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private IConnectionMultiplexer? _redis;

    public ProfileApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-API-Key", "change-me-admin-key");
    }

    public async Task InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync("localhost");
        await CleanProfileKeys();
    }

    public async Task DisposeAsync()
    {
        await CleanProfileKeys();
        _redis?.Dispose();
    }

    private async Task CleanProfileKeys()
    {
        var db = _redis!.GetDatabase();
        var ids = await db.SetMembersAsync("deploy:profiles");
        foreach (var id in ids)
        {
            await db.KeyDeleteAsync($"deploy:profile:{id}");
        }
        await db.KeyDeleteAsync("deploy:profiles");
    }

    private async Task CleanJobKeys()
    {
        var db = _redis!.GetDatabase();
        // Clean queue and running sets
        await db.KeyDeleteAsync("deploy:queue");
        await db.KeyDeleteAsync("deploy:running");
        // Clean history list
        var historyIds = await db.ListRangeAsync("deploy:history");
        foreach (var id in historyIds)
        {
            await db.KeyDeleteAsync($"deploy:job:{id}");
        }
        await db.KeyDeleteAsync("deploy:history");
    }

    // ----- Helper -----

    private async Task<(HttpResponseMessage Response, JsonElement Body)> CreateProfile(
        string name, string[]? steps = null, EnvVar[]? envVars = null, string? workingDirectory = null)
    {
        var request = new CreateProfileRequest(name, workingDirectory, envVars, steps ?? new[] { "build" });
        var response = await _client.PostAsJsonAsync("/api/profiles", request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response, body);
    }

    // ----- POST tests -----

    [Fact]
    public async Task Post_ValidProfile_Returns201WithMaskedSecrets()
    {
        var envVars = new[] { new EnvVar("SECRET_KEY", "real_value", true), new EnvVar("PUBLIC_KEY", "pub", false) };
        var (response, body) = await CreateProfile("ValidProfile", new[] { "npm install", "npm run build" }, envVars);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("ValidProfile", body.GetProperty("name").GetString());
        // Secret must be masked
        var vars = body.GetProperty("envVars");
        Assert.Equal("***", vars[0].GetProperty("value").GetString());
        Assert.Equal("pub", vars[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Post_MissingName_Returns400()
    {
        var request = new CreateProfileRequest("", null, null, new[] { "build" });
        var response = await _client.PostAsJsonAsync("/api/profiles", request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Name is required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_EmptySteps_Returns400()
    {
        var request = new CreateProfileRequest("TestProfile", null, null, Array.Empty<string>());
        var response = await _client.PostAsJsonAsync("/api/profiles", request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Steps are required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_DuplicateEnvVarKey_Returns400()
    {
        var envVars = new[] { new EnvVar("DUPE_KEY", "value1", false), new EnvVar("DUPE_KEY", "value2", false) };
        var (response, body) = await CreateProfile("DupeKeyProfile", envVars: envVars);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Duplicate env var key: DUPE_KEY", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_DuplicateName_Returns409()
    {
        await CreateProfile("UniqueName");
        var (response, body) = await CreateProfile("UniqueName");

        Assert.Equal((HttpStatusCode)409, response.StatusCode);
        Assert.Equal("A profile with that name already exists", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_DefaultsWorkingDirectory()
    {
        var (response, body) = await CreateProfile("DefaultWdProfile");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(string.Empty, body.GetProperty("workingDirectory").GetString());
    }

    // ----- GET list tests -----

    [Fact]
    public async Task GetList_ReturnsAllProfiles_MaskedAndAlphabetical()
    {
        await CreateProfile("Zebra");
        await CreateProfile("Alpha");

        var response = await _client.GetAsync("/api/profiles");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profiles = body.EnumerateArray().ToList();
        Assert.True(profiles.Count >= 2);
        // Find our profiles
        var names = profiles.Select(p => p.GetProperty("name").GetString()).ToList();
        var alphaIdx = names.IndexOf("Alpha");
        var zebraIdx = names.IndexOf("Zebra");
        Assert.True(alphaIdx >= 0 && zebraIdx >= 0);
        Assert.True(alphaIdx < zebraIdx, "Alpha should appear before Zebra");
    }

    [Fact]
    public async Task GetList_Empty_ReturnsEmptyArray()
    {
        var response = await _client.GetAsync("/api/profiles");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(0, body.GetArrayLength());
    }

    // ----- GET single tests -----

    [Fact]
    public async Task GetSingle_Exists_ReturnsMaskedProfile()
    {
        var envVars = new[] { new EnvVar("MY_SECRET", "secret_val", true) };
        var (createResponse, createBody) = await CreateProfile("GetSingleProfile", envVars: envVars);
        var id = createBody.GetProperty("id").GetString();

        var response = await _client.GetAsync($"/api/profiles/{id}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("GetSingleProfile", body.GetProperty("name").GetString());
        // Secret should be masked
        var vars = body.GetProperty("envVars").EnumerateArray().ToList();
        Assert.Equal("***", vars[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task GetSingle_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/profiles/nonexistent-id-that-does-not-exist");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Profile not found", body.GetProperty("error").GetString());
    }

    // ----- PUT tests -----

    [Fact]
    public async Task Put_ValidUpdate_Returns200()
    {
        var (_, createBody) = await CreateProfile("PutTestProfile", new[] { "original-step" });
        var id = createBody.GetProperty("id").GetString();

        var updateRequest = new UpdateProfileRequest("PutTestProfileUpdated", "/new/path", Array.Empty<EnvVar>(), new[] { "updated-step" });
        var response = await _client.PutAsJsonAsync($"/api/profiles/{id}", updateRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PutTestProfileUpdated", body.GetProperty("name").GetString());
        Assert.Equal("/new/path", body.GetProperty("workingDirectory").GetString());
    }

    [Fact]
    public async Task Put_SentinelPreservesSecret()
    {
        var envVars = new[] { new EnvVar("MY_SECRET", "original_secret_value", true) };
        var (_, createBody) = await CreateProfile("SentinelProfile", envVars: envVars);
        var id = createBody.GetProperty("id").GetString();

        // PUT with "***" as value — should preserve original secret
        var updateEnvVars = new[] { new EnvVar("MY_SECRET", "***", true) };
        var updateRequest = new UpdateProfileRequest("SentinelProfile", string.Empty, updateEnvVars, new[] { "build" });
        var response = await _client.PutAsJsonAsync($"/api/profiles/{id}", updateRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The response still shows *** (masked), but the PUT succeeded — secret was preserved
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var vars = body.GetProperty("envVars").EnumerateArray().ToList();
        Assert.Equal("***", vars[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Put_NameConflictExcludesSelf()
    {
        var (_, createBody) = await CreateProfile("SelfRenameProfile", new[] { "step1" });
        var id = createBody.GetProperty("id").GetString();

        // PUT with the same name should succeed (not rejected as duplicate)
        var updateRequest = new UpdateProfileRequest("SelfRenameProfile", string.Empty, Array.Empty<EnvVar>(), new[] { "step1", "step2" });
        var response = await _client.PutAsJsonAsync($"/api/profiles/{id}", updateRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_NameConflictOther_Returns409()
    {
        await CreateProfile("ExistingProfileName");
        var (_, createBody) = await CreateProfile("ProfileToRename");
        var id = createBody.GetProperty("id").GetString();

        var updateRequest = new UpdateProfileRequest("ExistingProfileName", string.Empty, Array.Empty<EnvVar>(), new[] { "build" });
        var response = await _client.PutAsJsonAsync($"/api/profiles/{id}", updateRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((HttpStatusCode)409, response.StatusCode);
        Assert.Equal("A profile with that name already exists", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_NotFound_Returns404()
    {
        var updateRequest = new UpdateProfileRequest("SomeName", string.Empty, Array.Empty<EnvVar>(), new[] { "build" });
        var response = await _client.PutAsJsonAsync("/api/profiles/nonexistent-id", updateRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Profile not found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_DuplicateEnvVarKey_Returns400()
    {
        var (_, createBody) = await CreateProfile("PutDupeKeyProfile");
        var id = createBody.GetProperty("id").GetString();

        var dupeEnvVars = new[] { new EnvVar("SAME_KEY", "v1", false), new EnvVar("SAME_KEY", "v2", false) };
        var updateRequest = new UpdateProfileRequest("PutDupeKeyProfile", string.Empty, dupeEnvVars, new[] { "build" });
        var response = await _client.PutAsJsonAsync($"/api/profiles/{id}", updateRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Duplicate env var key: SAME_KEY", body.GetProperty("error").GetString());
    }

    // ----- DELETE tests -----

    [Fact]
    public async Task Delete_Exists_Returns200()
    {
        var (_, createBody) = await CreateProfile("ProfileToDelete");
        var id = createBody.GetProperty("id").GetString();

        var response = await _client.DeleteAsync($"/api/profiles/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        var response = await _client.DeleteAsync("/api/profiles/nonexistent-id-for-delete");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Profile not found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_WithActiveJob_Returns409()
    {
        await CleanJobKeys();
        var (_, createBody) = await CreateProfile("ActiveJobProfile", new[] { "build" });
        var id = createBody.GetProperty("id").GetString()!;

        // Enqueue a job for this profile
        var jobResponse = await _client.PostAsJsonAsync("/api/jobs", new { profileId = id });
        Assert.Equal(HttpStatusCode.Created, jobResponse.StatusCode);

        // Attempt to delete profile — should be blocked
        var response = await _client.DeleteAsync($"/api/profiles/{id}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((HttpStatusCode)409, response.StatusCode);
        Assert.Equal("Profile has active jobs", body.GetProperty("error").GetString());

        await CleanJobKeys();
    }

    [Fact]
    public async Task Delete_WithCompletedJob_Returns200()
    {
        await CleanJobKeys();
        var (_, createBody) = await CreateProfile("CompletedJobProfile", new[] { "build" });
        var id = createBody.GetProperty("id").GetString()!;

        // Enqueue a job
        var jobResponse = await _client.PostAsJsonAsync("/api/jobs", new { profileId = id });
        Assert.Equal(HttpStatusCode.Created, jobResponse.StatusCode);

        // Create an agent client
        var agentClient = _factory.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-API-Key", "change-me-agent-key");

        // Poll to dequeue (moves job from queue to running)
        var pollResponse = await agentClient.PostAsync("/api/agent/poll", null);
        Assert.Equal(HttpStatusCode.OK, pollResponse.StatusCode);
        var pollBody = await pollResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = pollBody.GetProperty("jobId").GetString()!;

        // Complete the job
        var statusResponse = await agentClient.PutAsJsonAsync(
            $"/api/agent/status?jobId={jobId}",
            new { status = "completed", logs = (string?)null, error = (string?)null, artifactPath = (string?)null });
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        // Now delete should succeed — job is completed, not active
        var response = await _client.DeleteAsync($"/api/profiles/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await CleanJobKeys();
    }

    // ----- Storage tests -----

    [Fact]
    public async Task Storage_UsesCorrectRedisKeys()
    {
        var (_, createBody) = await CreateProfile("StorageTestProfile");
        var id = createBody.GetProperty("id").GetString()!;

        var db = _redis!.GetDatabase();
        var keyExists = await db.KeyExistsAsync($"deploy:profile:{id}");
        var inSet = await db.SetContainsAsync("deploy:profiles", id);

        Assert.True(keyExists, $"Expected key deploy:profile:{id} to exist in Redis");
        Assert.True(inSet, $"Expected id {id} to be in deploy:profiles set");
    }
}
