using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PrepForge.DeployAgent.Tests")]

namespace PrepForge.DeployAgent;

public record StepResult(bool Success, string? Error);

public class StepRunner
{
    private readonly DeployAgentSettings _settings;
    private readonly ILogger<StepRunner> _logger;
    private const int MaxLogLines = 1000;
    private Action<string>? _onLogLine;

    public StepRunner(IOptions<DeployAgentSettings> settings, ILogger<StepRunner> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public void SetLogCallback(Action<string>? callback) => _onLogLine = callback;

    public async Task<StepResult> RunStepsAsync(ProfileSnapshot snapshot, CancellationToken ct = default)
    {
        EmitLine($"--- Running profile: {snapshot.Name} ({snapshot.Steps.Length} steps) ---");

        for (int i = 0; i < snapshot.Steps.Length; i++)
        {
            var step = snapshot.Steps[i];
            var redactedStep = RedactSecrets(step, snapshot.EnvVars);
            EmitLine($"--- Step {i + 1}: {redactedStep} ---");

            var (success, exitCode, error) = await RunStepAsync(step, snapshot, ct);

            if (!success)
            {
                EmitLine($"--- Step {i + 1} FAILED (exit code {exitCode}) ---");
                return new StepResult(false, error);
            }
        }

        EmitLine("--- All steps completed ---");
        return new StepResult(true, null);
    }

    private void EmitLine(string line)
    {
        _logger.LogInformation("{Line}", line);
        _onLogLine?.Invoke(line);
    }

    private async Task<(bool Success, int ExitCode, string? Error)> RunStepAsync(
        string step, ProfileSnapshot snapshot, CancellationToken ct)
    {
        var logLines = new LinkedList<string>();
        var mergedEnv = BuildMergedEnvironment(snapshot.EnvVars);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c \"{step.Replace("\"", "\\\"")}\"",
                WorkingDirectory = snapshot.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Apply merged environment — psi.Environment starts populated from
            // current process when UseShellExecute=false. Overlay profile vars on top.
            foreach (var kvp in mergedEnv)
                psi.Environment[kvp.Key] = kvp.Value;

            using var process = Process.Start(psi);
            if (process is null)
                return (false, -1, $"Failed to start process for step: {step}");

            var stdoutTask = ReadStreamAsync(process.StandardOutput, logLines, "stdout", snapshot.EnvVars, ct);
            var stderrTask = ReadStreamAsync(process.StandardError, logLines, "stderr", snapshot.EnvVars, ct);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return (false, process.ExitCode, $"Step failed: {step} (exit code {process.ExitCode})");

            return (true, 0, null);
        }
        catch (OperationCanceledException)
        {
            return (false, -1, $"Step cancelled: {step}");
        }
        catch (Exception ex)
        {
            return (false, -1, $"Step error: {step} — {ex.Message}");
        }
    }

    private async Task ReadStreamAsync(StreamReader reader, LinkedList<string> logLines,
        string prefix, EnvVar[] secrets, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } rawLine)
        {
            var line = RedactSecrets(rawLine, secrets);
            _logger.LogInformation("[{Prefix}] {Line}", prefix, line);
            lock (logLines)
            {
                logLines.AddLast(line);
                if (logLines.Count > MaxLogLines)
                    logLines.RemoveFirst();
            }
            _onLogLine?.Invoke(line);
        }
    }

    internal static string RedactSecrets(string line, EnvVar[] envVars)
    {
        // Pass 1: Literal value replacement
        foreach (var ev in envVars)
        {
            if (ev.IsSecret && !string.IsNullOrEmpty(ev.Value))
                line = line.Replace(ev.Value, "***", StringComparison.Ordinal);
        }

        // Pass 2: Regex — KEY=value and "KEY":"value" patterns
        foreach (var ev in envVars)
        {
            if (!ev.IsSecret) continue;
            var key = Regex.Escape(ev.Key);
            // KEY=anything-until-whitespace-or-end
            line = Regex.Replace(line, $@"{key}=[^\s]+", $"{ev.Key}=***");
            // "KEY":"value" JSON style
            line = Regex.Replace(line, $@"""{key}""\s*:\s*""[^""]*""", $@"""{ev.Key}"":""***""");
        }

        return line;
    }

    internal static Dictionary<string, string?> BuildMergedEnvironment(EnvVar[] profileEnvVars)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            merged[(string)entry.Key] = entry.Value as string;

        foreach (var envVar in profileEnvVars)
            merged[envVar.Key] = envVar.Value; // profile wins

        return merged;
    }
}
