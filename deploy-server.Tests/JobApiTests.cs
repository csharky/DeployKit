using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PrepForge.DeployServer;
using StackExchange.Redis;
using Xunit;

namespace PrepForge.DeployServer.Tests;

public class JobApiTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly HttpClient _adminClient;
    private readonly HttpClient _agentClient;
    private IConnectionMultiplexer? _redis;

    public JobApiTests(WebApplicationFactory<Program> factory)
    {
        _adminClient = factory.CreateClient();
        _adminClient.DefaultRequestHeaders.Add("X-API-Key", "change-me-admin-key");

        _agentClient = factory.CreateClient();
        _agentClient.DefaultRequestHeaders.Add("X-API-Key", "change-me-agent-key");
    }

    public async Task InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync("localhost");
        await CleanKeys();
    }

    public async Task DisposeAsync()
    {
        await CleanKeys();
        _redis?.Dispose();
    }

    private async Task CleanKeys()
    {
        var db = _redis!.GetDatabase();
        // Clean profiles
        var profileIds = await db.SetMembersAsync("deploy:profiles");
        foreach (var id in profileIds)
            await db.KeyDeleteAsync($"deploy:profile:{id}");
        await db.KeyDeleteAsync("deploy:profiles");
        // Clean jobs from queue and history
        await db.KeyDeleteAsync("deploy:queue");
        await db.KeyDeleteAsync("deploy:running");
        // Clean history entries
        var historyIds = await db.ListRangeAsync("deploy:history");
        foreach (var id in historyIds)
            await db.KeyDeleteAsync($"deploy:job:{id}");
        await db.KeyDeleteAsync("deploy:history");
    }

    // ----- Helpers -----

    private async Task<string> CreateTestProfile(string name, EnvVar[]? envVars = null)
    {
        var request = new CreateProfileRequest(name, null, envVars, new[] { "echo build" });
        var response = await _adminClient.PostAsJsonAsync("/api/profiles", request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> SubmitJob(string profileId)
    {
        var response = await _adminClient.PostAsJsonAsync("/api/jobs", new CreateJobRequest(profileId));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response, body);
    }

    // ----- Test: JOB-01 — Valid profileId returns 201 -----

    [Fact]
    public async Task Post_ValidProfileId_Returns201()
    {
        var profileId = await CreateTestProfile("Job01Profile");

        var (response, body) = await SubmitJob(profileId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(profileId, body.GetProperty("profileId").GetString());
        Assert.Equal("pending", body.GetProperty("status").GetString());
    }

    // ----- Test: JOB-02 — Snapshot embedded with real (unmasked) env var values -----

    [Fact]
    public async Task Post_ValidProfileId_SnapshotEmbedded()
    {
        var envVars = new[]
        {
            new EnvVar("SECRET", "real_secret_value", true),
            new EnvVar("PUBLIC", "pub_value", false)
        };
        var request = new CreateProfileRequest(
            "SnapshotTest",
            "/test/dir",
            envVars,
            new[] { "npm install", "npm run build" });

        var createResponse = await _adminClient.PostAsJsonAsync("/api/profiles", request);
        createResponse.EnsureSuccessStatusCode();
        var createBody = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var profileId = createBody.GetProperty("id").GetString()!;

        var (response, body) = await SubmitJob(profileId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var snapshot = body.GetProperty("profileSnapshot");
        Assert.Equal("SnapshotTest", snapshot.GetProperty("name").GetString());
        Assert.Equal("/test/dir", snapshot.GetProperty("workingDirectory").GetString());

        var steps = snapshot.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal("npm install", steps[0].GetString());
        Assert.Equal("npm run build", steps[1].GetString());

        var snapshotEnvVars = snapshot.GetProperty("envVars").EnumerateArray().ToList();
        Assert.Equal("real_secret_value", snapshotEnvVars[0].GetProperty("value").GetString());
        Assert.True(snapshotEnvVars[0].GetProperty("isSecret").GetBoolean());
        Assert.Equal("pub_value", snapshotEnvVars[1].GetProperty("value").GetString());
    }

    // ----- Test: JOB-03 — Non-existent profileId returns 400 -----

    [Fact]
    public async Task Post_NonExistentProfileId_Returns400()
    {
        var (response, body) = await SubmitJob("nonexistent-profile-id-12345");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Profile not found", body.GetProperty("error").GetString());
    }

    // ----- Test: JOB-04 — Agent poll response contains full snapshot with real env var values -----

    [Fact]
    public async Task Poll_ReturnsJobWithSnapshot()
    {
        var envVars = new[] { new EnvVar("AGENT_SECRET", "agent_secret_val", true) };
        var profileId = await CreateTestProfile("AgentPollProfile", envVars);

        await SubmitJob(profileId);

        var pollResponse = await _agentClient.PostAsync("/api/agent/poll", null);
        Assert.Equal(HttpStatusCode.OK, pollResponse.StatusCode);

        var pollBody = await pollResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(profileId, pollBody.GetProperty("profileId").GetString());

        var snapshot = pollBody.GetProperty("profileSnapshot");
        Assert.NotEqual(JsonValueKind.Null, snapshot.ValueKind);

        var snapshotEnvVars = snapshot.GetProperty("envVars").EnumerateArray().ToList();
        Assert.Equal("agent_secret_val", snapshotEnvVars[0].GetProperty("value").GetString());
    }

    // ----- Test: Snapshot immutability — profile edits after enqueue do not alter stored snapshot -----

    [Fact]
    public async Task Post_SnapshotImmutableAfterProfileEdit()
    {
        var profileId = await CreateTestProfile("OriginalName");

        var (submitResponse, submitBody) = await SubmitJob(profileId);
        Assert.Equal(HttpStatusCode.Created, submitResponse.StatusCode);
        var jobId = submitBody.GetProperty("jobId").GetString()!;

        // Update the profile after enqueue
        var updateRequest = new UpdateProfileRequest(
            "RenamedProfile",
            "/new/path",
            Array.Empty<EnvVar>(),
            new[] { "new-step" });
        await _adminClient.PutAsJsonAsync($"/api/profiles/{profileId}", updateRequest);

        // Fetch the stored job — snapshot must reflect original profile
        var jobResponse = await _adminClient.GetAsync($"/api/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, jobResponse.StatusCode);
        var jobBody = await jobResponse.Content.ReadFromJsonAsync<JsonElement>();

        var snapshot = jobBody.GetProperty("profileSnapshot");
        Assert.Equal("OriginalName", snapshot.GetProperty("name").GetString());

        var steps = snapshot.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal("echo build", steps[0].GetString());
    }
}
