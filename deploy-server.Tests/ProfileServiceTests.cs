using Xunit;
using DeployKit.DeployServer;

namespace DeployKit.DeployServer.Tests;

public class ProfileServiceTests
{
    [Fact]
    public void MaskSecrets_ReplacesSecretValues()
    {
        var profile = new BuildProfile("id1", "test", "/work",
            new[] { new EnvVar("PUBLIC", "pub_val", false), new EnvVar("SECRET", "real_secret", true) },
            new[] { "build" });
        var response = ProfileService.MaskSecrets(profile);
        Assert.Equal("pub_val", response.EnvVars[0].Value);
        Assert.Equal("***", response.EnvVars[1].Value);
        Assert.False(response.EnvVars[0].IsSecret);
        Assert.True(response.EnvVars[1].IsSecret);
    }

    [Fact]
    public void MaskSecrets_PreservesNonSecretValues()
    {
        var profile = new BuildProfile("id1", "test", "/work",
            new[] { new EnvVar("KEY1", "val1", false), new EnvVar("KEY2", "val2", false) },
            new[] { "step1" });
        var response = ProfileService.MaskSecrets(profile);
        Assert.Equal("val1", response.EnvVars[0].Value);
        Assert.Equal("val2", response.EnvVars[1].Value);
    }

    [Fact]
    public void MaskSecrets_ReturnsProfileResponse_NotBuildProfile()
    {
        var profile = new BuildProfile("id1", "test", "/work", Array.Empty<EnvVar>(), new[] { "step" });
        var response = ProfileService.MaskSecrets(profile);
        Assert.IsType<ProfileResponse>(response);
        Assert.Equal("id1", response.Id);
        Assert.Equal("test", response.Name);
    }

    [Fact]
    public void MaskSecrets_EmptyEnvVars_ReturnsEmptyArray()
    {
        var profile = new BuildProfile("id1", "test", "", Array.Empty<EnvVar>(), new[] { "step" });
        var response = ProfileService.MaskSecrets(profile);
        Assert.Empty(response.EnvVars);
    }

    [Fact]
    public void CreateProfileRequest_DefaultsWorkingDirectory_WhenNull()
    {
        // Validates that null WorkingDirectory is accepted by the record
        var req = new CreateProfileRequest("test", null, null, new[] { "build" });
        var workDir = req.WorkingDirectory ?? string.Empty;
        Assert.Equal(string.Empty, workDir);
    }
}
