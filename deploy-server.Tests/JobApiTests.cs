using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DeployKit.DeployServer;
using Xunit;

namespace DeployKit.DeployServer.Tests;

public class JobApiTests : IClassFixture<TestWebApplicationFactory>, IAsyncLifetime
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _adminClient;
    private readonly HttpClient _agentClient;

    public JobApiTests(TestWebApplicationFactory factory)
    {
        _factory = factory;

        _adminClient = factory.CreateClient();
        _adminClient.DefaultRequestHeaders.Add("X-API-Key", "change-me-admin-key");

        _agentClient = factory.CreateClient();
        _agentClient.DefaultRequestHeaders.Add("X-API-Key", "change-me-agent-key");
    }

    public Task InitializeAsync() => _factory.ClearAllDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ----- Helpers -----

    private async Task<string> CreateTestProfile(string name, EnvVar[]? envVars = null)
    {
        var request = new CreateProfileRequest(name, null, envVars, new[] { "echo build" });
        var response = await _adminClient.PostAsJsonAsync("/api/profiles", request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> SubmitJob(
        string profileId, EnvVar[]? envOverrides = null)
    {
        var response = await _adminClient.PostAsJsonAsync("/api/jobs",
            new CreateJobRequest(profileId, envOverrides));
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
            "SnapshotTest", "/test/dir", envVars, new[] { "npm install", "npm run build" });

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

        var updateRequest = new UpdateProfileRequest(
            "RenamedProfile", "/new/path", Array.Empty<EnvVar>(), new[] { "new-step" });
        await _adminClient.PutAsJsonAsync($"/api/profiles/{profileId}", updateRequest);

        var jobResponse = await _adminClient.GetAsync($"/api/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, jobResponse.StatusCode);
        var jobBody = await jobResponse.Content.ReadFromJsonAsync<JsonElement>();

        var snapshot = jobBody.GetProperty("profileSnapshot");
        Assert.Equal("OriginalName", snapshot.GetProperty("name").GetString());

        var steps = snapshot.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal("echo build", steps[0].GetString());
    }

    // ----- Test: EnvOverrides — override existing profile env var -----

    [Fact]
    public async Task Post_WithEnvOverrides_OverridesProfileEnvVar()
    {
        var envVars = new[]
        {
            new EnvVar("BUILD_MODE", "debug", false),
            new EnvVar("API_KEY", "original_key", true)
        };
        var profileId = await CreateTestProfile("OverrideTest", envVars);

        var overrides = new[] { new EnvVar("BUILD_MODE", "release", false) };
        var (response, body) = await SubmitJob(profileId, overrides);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var snapshotEnvVars = body.GetProperty("profileSnapshot")
            .GetProperty("envVars").EnumerateArray().ToList();

        var buildMode = snapshotEnvVars.First(e => e.GetProperty("key").GetString() == "BUILD_MODE");
        Assert.Equal("release", buildMode.GetProperty("value").GetString());

        var apiKey = snapshotEnvVars.First(e => e.GetProperty("key").GetString() == "API_KEY");
        Assert.Equal("original_key", apiKey.GetProperty("value").GetString());
    }

    // ----- Test: EnvOverrides — add new env var not in profile -----

    [Fact]
    public async Task Post_WithEnvOverrides_AddsNewEnvVar()
    {
        var envVars = new[] { new EnvVar("EXISTING", "value1", false) };
        var profileId = await CreateTestProfile("AddNewEnvTest", envVars);

        var overrides = new[] { new EnvVar("NEW_VAR", "new_value", false) };
        var (response, body) = await SubmitJob(profileId, overrides);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var snapshotEnvVars = body.GetProperty("profileSnapshot")
            .GetProperty("envVars").EnumerateArray().ToList();

        Assert.Equal(2, snapshotEnvVars.Count);
        Assert.Contains(snapshotEnvVars, e => e.GetProperty("key").GetString() == "EXISTING");
        Assert.Contains(snapshotEnvVars, e => e.GetProperty("key").GetString() == "NEW_VAR"
            && e.GetProperty("value").GetString() == "new_value");
    }

    // ----- Test: EnvOverrides — null overrides keeps original behavior -----

    [Fact]
    public async Task Post_WithoutEnvOverrides_KeepsOriginalEnvVars()
    {
        var envVars = new[] { new EnvVar("ORIGINAL", "orig_val", false) };
        var profileId = await CreateTestProfile("NoOverrideTest", envVars);

        var (response, body) = await SubmitJob(profileId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var snapshotEnvVars = body.GetProperty("profileSnapshot")
            .GetProperty("envVars").EnumerateArray().ToList();

        Assert.Single(snapshotEnvVars);
        Assert.Equal("ORIGINAL", snapshotEnvVars[0].GetProperty("key").GetString());
        Assert.Equal("orig_val", snapshotEnvVars[0].GetProperty("value").GetString());
    }

    // ----- Test: EnvOverrides — duplicate keys in overrides returns 400 -----

    [Fact]
    public async Task Post_WithDuplicateEnvOverrideKeys_Returns400()
    {
        var profileId = await CreateTestProfile("DupOverrideTest");

        var overrides = new[]
        {
            new EnvVar("SAME_KEY", "val1", false),
            new EnvVar("SAME_KEY", "val2", false)
        };
        var (response, body) = await SubmitJob(profileId, overrides);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Duplicate env override key: SAME_KEY", body.GetProperty("error").GetString());
    }

    // ----- Test: Locked env var cannot be overridden -----

    [Fact]
    public async Task Post_WithLockedEnvVarOverride_Returns400()
    {
        var envVars = new[] { new EnvVar("LOCKED_VAR", "fixed_value", false, IsLocked: true) };
        var profileId = await CreateTestProfile("LockedVarProfile", envVars);

        var overrides = new[] { new EnvVar("LOCKED_VAR", "hacked_value", false) };
        var (response, body) = await SubmitJob(profileId, overrides);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Env var 'LOCKED_VAR' is locked and cannot be overridden", body.GetProperty("error").GetString());
    }

    // ----- Test: Unlocked env var can still be overridden alongside a locked one -----

    [Fact]
    public async Task Post_WithUnlockedEnvVarOverride_Succeeds()
    {
        var envVars = new[]
        {
            new EnvVar("LOCKED_VAR", "fixed_value", false, IsLocked: true),
            new EnvVar("FREE_VAR", "original", false),
        };
        var profileId = await CreateTestProfile("MixedLockProfile", envVars);

        var overrides = new[] { new EnvVar("FREE_VAR", "overridden", false) };
        var (response, body) = await SubmitJob(profileId, overrides);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var snapshotEnvVars = body.GetProperty("profileSnapshot")
            .GetProperty("envVars").EnumerateArray().ToList();
        var freeVar = snapshotEnvVars.First(e => e.GetProperty("key").GetString() == "FREE_VAR");
        Assert.Equal("overridden", freeVar.GetProperty("value").GetString());
    }
}
