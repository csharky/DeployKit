using System.Text.Json;
using Xunit;
using PrepForge.DeployServer;

namespace PrepForge.DeployServer.Tests;

public class ProfileModelTests
{
    [Fact]
    public void BuildProfile_HasAllRequiredFields()
    {
        var profile = new BuildProfile("abc123", "MyBuild", "/home/agent",
            new[] { new EnvVar("KEY", "val", false) },
            new[] { "npm install" });
        Assert.Equal("abc123", profile.Id);
        Assert.Equal("MyBuild", profile.Name);
        Assert.Equal("/home/agent", profile.WorkingDirectory);
        Assert.Single(profile.EnvVars);
        Assert.Single(profile.Steps);
    }

    [Fact]
    public void EnvVar_HasKeyValueIsSecret()
    {
        var env = new EnvVar("API_KEY", "secret123", true);
        Assert.Equal("API_KEY", env.Key);
        Assert.Equal("secret123", env.Value);
        Assert.True(env.IsSecret);
    }

    [Fact]
    public void Steps_SerialiseAsOrderedStringArray()
    {
        var steps = new[] { "npm install", "npm run build", "xcodebuild -scheme MyApp" };
        var profile = new BuildProfile("id1", "test", "", Array.Empty<EnvVar>(), steps);
        var json = JsonSerializer.Serialize(profile);
        var deserialized = JsonSerializer.Deserialize<BuildProfile>(json);
        Assert.Equal(steps, deserialized!.Steps);
    }

    [Fact]
    public void EnvVar_WithExpression_ReplacesValue()
    {
        var env = new EnvVar("KEY", "real_value", true);
        var masked = env with { Value = "***" };
        Assert.Equal("***", masked.Value);
        Assert.True(masked.IsSecret);
        Assert.Equal("KEY", masked.Key);
    }

    [Fact]
    public void CreateProfileRequest_AllowsNullOptionalFields()
    {
        var req = new CreateProfileRequest("MyBuild", null, null, new[] { "build" });
        Assert.Null(req.WorkingDirectory);
        Assert.Null(req.EnvVars);
    }
}
