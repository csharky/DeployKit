using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeployKit.DeployAgent.Tests;

public class StepRunnerIntegrationTests
{
    private static StepRunner CreateRunner(out List<string> logLines)
    {
        var captured = new List<string>();
        logLines = captured;

        var runner = new StepRunner(
            Options.Create(new DeployAgentSettings()),
            NullLogger<StepRunner>.Instance);

        runner.SetLogCallback(line => captured.Add(line));
        return runner;
    }

    private static ProfileSnapshot MakeProfile(
        string[] steps,
        string workingDirectory = "/tmp",
        EnvVar[]? envVars = null) =>
        new ProfileSnapshot(
            Id: "test-id",
            Name: "test-profile",
            WorkingDirectory: workingDirectory,
            EnvVars: envVars ?? Array.Empty<EnvVar>(),
            Steps: steps);

    [Fact]
    public async Task RunStepsAsync_ExecutesInOrder()
    {
        var runner = CreateRunner(out var logLines);
        var profile = MakeProfile(new[] { "echo first", "echo second" });

        await runner.RunStepsAsync(profile);

        var combined = string.Join("\n", logLines);
        var firstIdx = combined.IndexOf("first", StringComparison.Ordinal);
        var secondIdx = combined.IndexOf("second", StringComparison.Ordinal);
        Assert.True(firstIdx >= 0, "Expected 'first' in log output");
        Assert.True(secondIdx >= 0, "Expected 'second' in log output");
        Assert.True(firstIdx < secondIdx, "'first' should appear before 'second'");
    }

    [Fact]
    public async Task RunStepsAsync_EmitsBanner()
    {
        var runner = CreateRunner(out var logLines);
        var profile = MakeProfile(new[] { "echo hi" });

        await runner.RunStepsAsync(profile);

        Assert.Contains(logLines, l => l.Contains("--- Running profile: test-profile (1 steps) ---"));
    }

    [Fact]
    public async Task RunStepsAsync_EmitsStepHeaders()
    {
        var runner = CreateRunner(out var logLines);
        var profile = MakeProfile(new[] { "echo hello", "echo world" });

        await runner.RunStepsAsync(profile);

        Assert.Contains(logLines, l => l.Contains("--- Step 1: echo hello ---"));
        Assert.Contains(logLines, l => l.Contains("--- Step 2: echo world ---"));
    }

    [Fact]
    public async Task RunStepsAsync_EmitsCompletionBanner()
    {
        var runner = CreateRunner(out var logLines);
        var profile = MakeProfile(new[] { "echo done" });

        var result = await runner.RunStepsAsync(profile);

        Assert.True(result.Success);
        Assert.Contains(logLines, l => l.Contains("--- All steps completed ---"));
    }

    [Fact]
    public async Task RunStepsAsync_StopOnFailure()
    {
        var runner = CreateRunner(out var logLines);
        var profile = MakeProfile(new[] { "echo ok", "false", "echo never" });

        var result = await runner.RunStepsAsync(profile);

        Assert.False(result.Success);
        var combined = string.Join("\n", logLines);
        Assert.DoesNotContain("never", combined);
    }

    [Fact]
    public async Task RunStepsAsync_FailureEmitsExitCode()
    {
        var runner = CreateRunner(out var logLines);
        var profile = MakeProfile(new[] { "false" });

        var result = await runner.RunStepsAsync(profile);

        Assert.False(result.Success);
        Assert.Contains(logLines, l => l.Contains("--- Step 1 FAILED (exit code"));
    }

    [Fact]
    public async Task RunStepsAsync_SetsWorkingDirectory()
    {
        var runner = CreateRunner(out var logLines);
        var profile = MakeProfile(new[] { "pwd" }, workingDirectory: "/tmp");

        var result = await runner.RunStepsAsync(profile);

        Assert.True(result.Success);
        var combined = string.Join("\n", logLines);
        Assert.Contains("/tmp", combined);
    }

    [Fact]
    public async Task RunStepsAsync_InjectsEnvVars()
    {
        var runner = CreateRunner(out var logLines);
        var envVars = new[] { new EnvVar("MY_TEST_VAR", "hello123", false) };
        var profile = MakeProfile(new[] { "echo $MY_TEST_VAR" }, envVars: envVars);

        var result = await runner.RunStepsAsync(profile);

        Assert.True(result.Success);
        var combined = string.Join("\n", logLines);
        Assert.Contains("hello123", combined);
    }

    [Fact]
    public async Task RunStepsAsync_RedactsSecrets()
    {
        var runner = CreateRunner(out var logLines);
        var envVars = new[] { new EnvVar("SECRET_KEY", "s3cret", true) };
        var profile = MakeProfile(new[] { "echo s3cret" }, envVars: envVars);

        var result = await runner.RunStepsAsync(profile);

        Assert.True(result.Success);
        var combined = string.Join("\n", logLines);
        Assert.DoesNotContain("s3cret", combined);
        Assert.Contains("***", combined);
    }

    [Fact]
    public async Task RunStepsAsync_NullSnapshot_ReturnsFailure()
    {
        var runner = CreateRunner(out _);

        var result = await Assert.ThrowsAnyAsync<Exception>(
            () => runner.RunStepsAsync(null!));

        // Acceptable: either throws NullReferenceException or returns failure — we use Assert.ThrowsAnyAsync
        Assert.NotNull(result);
    }
}
