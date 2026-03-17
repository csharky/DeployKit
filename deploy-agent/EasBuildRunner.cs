using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace PrepForge.DeployAgent;

public record BuildResult(bool Success, string Logs, string? ArtifactPath, string? Error);

public class EasBuildRunner
{
    private readonly DeployAgentSettings _settings;
    private readonly ILogger<EasBuildRunner> _logger;
    private const int MaxLogLines = 200;

    public EasBuildRunner(IOptions<DeployAgentSettings> settings, ILogger<EasBuildRunner> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<BuildResult> BuildAsync(string profile, BuildPlatform platform, CancellationToken ct = default)
    {
        var outputDir = Path.GetFullPath(_settings.BuildOutputPath, _settings.ProjectPath);
        Directory.CreateDirectory(outputDir);

        // Clean previous build output
        foreach (var file in Directory.GetFiles(outputDir, "*.ipa"))
            File.Delete(file);
        foreach (var file in Directory.GetFiles(outputDir, "*.tar.gz"))
            File.Delete(file);

        var platformArg = platform.ToString().ToLowerInvariant();
        var args = $"build --local --profile {profile} --platform {platformArg} --non-interactive --output {outputDir}";
        _logger.LogInformation("Running: eas {Args}", args);

        var result = await RunProcessAsync("eas", args, _settings.ProjectPath, ct);

        string? artifactPath = null;
        if (result.Success)
        {
            // Find the built artifact
            var ipaFiles = Directory.GetFiles(outputDir, "*.ipa");
            var tarFiles = Directory.GetFiles(outputDir, "*.tar.gz");

            if (ipaFiles.Length > 0)
                artifactPath = ipaFiles[0];
            else if (tarFiles.Length > 0)
                artifactPath = tarFiles[0];

            if (artifactPath is not null)
                _logger.LogInformation("Build artifact: {Path}", artifactPath);
            else
                _logger.LogWarning("Build succeeded but no artifact found in {Dir}", outputDir);
        }

        return new BuildResult(result.Success, result.Logs, artifactPath, result.Error);
    }

    public async Task<BuildResult> SubmitAsync(string artifactPath, CancellationToken ct = default)
    {
        var args = $"submit --platform ios --path \"{artifactPath}\" --non-interactive";
        _logger.LogInformation("Running: eas {Args}", args);

        var result = await RunProcessAsync("eas", args, _settings.ProjectPath, ct);
        return new BuildResult(result.Success, result.Logs, artifactPath, result.Error);
    }

    private async Task<(bool Success, string Logs, string? Error)> RunProcessAsync(
        string command, string arguments, string workingDir, CancellationToken ct)
    {
        var logLines = new LinkedList<string>();
        var errorBuilder = new StringBuilder();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Inherit PATH so eas/node/xcode are available
            psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";

            using var process = Process.Start(psi);
            if (process is null)
                return (false, "", "Failed to start process");

            // Read stdout and stderr concurrently
            var stdoutTask = ReadStreamAsync(process.StandardOutput, logLines, ct);
            var stderrTask = ReadStreamAsync(process.StandardError, logLines, ct);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);

            var logs = string.Join('\n', logLines);
            var success = process.ExitCode == 0;

            if (!success)
                return (false, logs, $"Process exited with code {process.ExitCode}");

            return (true, logs, null);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Join('\n', logLines), "Build was cancelled");
        }
        catch (Exception ex)
        {
            return (false, string.Join('\n', logLines), ex.Message);
        }
    }

    private static async Task ReadStreamAsync(StreamReader reader, LinkedList<string> logLines, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lock (logLines)
            {
                logLines.AddLast(line);
                if (logLines.Count > MaxLogLines)
                    logLines.RemoveFirst();
            }
        }
    }
}
