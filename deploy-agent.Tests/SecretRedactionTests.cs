using Xunit;

namespace DeployKit.DeployAgent.Tests;

public class SecretRedactionTests
{
    // --- RedactSecrets tests ---

    [Fact]
    public void RedactSecrets_LiteralReplace()
    {
        var envVars = new[] { new EnvVar("API_TOKEN", "abc123", true) };
        var result = StepRunner.RedactSecrets("token is abc123 here", envVars);
        Assert.Equal("token is *** here", result);
    }

    [Fact]
    public void RedactSecrets_KeyValuePattern()
    {
        var envVars = new[] { new EnvVar("API_TOKEN", "abc123", true) };
        var result = StepRunner.RedactSecrets("API_TOKEN=abc123 other", envVars);
        Assert.Equal("API_TOKEN=*** other", result);
    }

    [Fact]
    public void RedactSecrets_JsonPattern()
    {
        var envVars = new[] { new EnvVar("API_TOKEN", "abc123", true) };
        var result = StepRunner.RedactSecrets("{\"API_TOKEN\":\"abc123\"}", envVars);
        Assert.Equal("{\"API_TOKEN\":\"***\"}", result);
    }

    [Fact]
    public void RedactSecrets_EmptyValue_NoCorruption()
    {
        var envVars = new[] { new EnvVar("EMPTY_SECRET", "", true) };
        var line = "some important log line";
        var result = StepRunner.RedactSecrets(line, envVars);
        Assert.Equal(line, result);
    }

    [Fact]
    public void RedactSecrets_NonSecretIgnored()
    {
        var envVars = new[] { new EnvVar("PUBLIC", "val", false) };
        var result = StepRunner.RedactSecrets("output: val here", envVars);
        Assert.Equal("output: val here", result);
    }

    [Fact]
    public void RedactSecrets_MultipleSecrets()
    {
        var envVars = new[]
        {
            new EnvVar("SECRET_A", "alpha", true),
            new EnvVar("SECRET_B", "beta", true)
        };
        var result = StepRunner.RedactSecrets("val alpha and beta together", envVars);
        Assert.Equal("val *** and *** together", result);
    }

    // --- BuildMergedEnvironment tests ---

    [Fact]
    public void BuildMergedEnvironment_ProfileOverridesAmbient()
    {
        // PATH is always set in the ambient environment on any OS
        var originalPath = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin";
        var profileEnvVars = new[] { new EnvVar("PATH", "/custom/bin", false) };

        var result = StepRunner.BuildMergedEnvironment(profileEnvVars);

        Assert.True(result.ContainsKey("PATH"));
        Assert.Equal("/custom/bin", result["PATH"]);
    }

    [Fact]
    public void BuildMergedEnvironment_AmbientPreserved()
    {
        // HOME is set on macOS/Linux; on Windows it may be USERPROFILE
        // Use a definitely-ambient var: PATH
        var profileEnvVars = Array.Empty<EnvVar>();

        var result = StepRunner.BuildMergedEnvironment(profileEnvVars);

        // PATH should be present from ambient
        Assert.True(result.ContainsKey("PATH"), "Ambient PATH should be preserved in merged environment");
    }

    [Fact]
    public void BuildMergedEnvironment_ProfileAddsNew()
    {
        var uniqueKey = "PREPFORGE_TEST_UNIQUE_XYZ_" + Guid.NewGuid().ToString("N");
        var profileEnvVars = new[] { new EnvVar(uniqueKey, "unique_value", false) };

        var result = StepRunner.BuildMergedEnvironment(profileEnvVars);

        Assert.True(result.ContainsKey(uniqueKey), "Profile-only var should be added to merged environment");
        Assert.Equal("unique_value", result[uniqueKey]);
    }
}
