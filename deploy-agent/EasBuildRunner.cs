using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace PrepForge.DeployAgent;

public record BuildResult(bool Success, string Logs, string? ArtifactPath, string? Error);

public class EasBuildRunner
{
    private readonly DeployAgentSettings _settings;
    private readonly ILogger<EasBuildRunner> _logger;
    private const int MaxLogLines = 1000;
    private Action<string>? _onLogLine;

    public EasBuildRunner(IOptions<DeployAgentSettings> settings, ILogger<EasBuildRunner> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public void SetLogCallback(Action<string>? callback) => _onLogLine = callback;

    public async Task<BuildResult> BuildAsync(string profile, BuildPlatform platform, CancellationToken ct = default)
    {
        var outputDir = Path.GetFullPath(_settings.BuildOutputPath, _settings.ProjectPath);
        Directory.CreateDirectory(outputDir);

        // Clean previous build output
        foreach (var file in Directory.GetFiles(outputDir, "*.ipa"))
            File.Delete(file);
        foreach (var file in Directory.GetFiles(outputDir, "*.tar.gz"))
            File.Delete(file);

        // Install dependencies before building
        _logger.LogInformation("Installing npm dependencies...");
        var installResult = await RunProcessAsync("npm", "install", _settings.ProjectPath, ct);
        if (!installResult.Success)
            return new BuildResult(false, installResult.Logs, null, $"npm install failed: {installResult.Error}");

        // Unlock keychain for iOS builds
        _logger.LogInformation("Updating security...");
        var securityResult = await RunProcessAsync("security", $"unlock-keychain -p \"{_settings.KeychainPassword}\" /Users/uliasulejmanova/Library/Keychains/login.keychain-db", _settings.ProjectPath, ct);
        if (!securityResult.Success)
            return new BuildResult(false, securityResult.Logs, null, $"security unlock-keychain failed: {securityResult.Error}");

        var platformArg = platform.ToString().ToLowerInvariant();
        string args;
        string? outputFile = null;

        if (platform == BuildPlatform.Ios)
        {
            outputFile = Path.Combine(outputDir, $"build-{profile}-ios.ipa");
            if (File.Exists(outputFile)) File.Delete(outputFile);
            args = $"build --local --profile {profile} --platform ios --non-interactive --output \"{outputFile}\"";
        }
        else
        {
            // Android can produce .apk or .aab depending on profile — EAS decides, so we scan after
            foreach (var file in Directory.GetFiles(outputDir, "*.apk").Concat(Directory.GetFiles(outputDir, "*.aab")))
                File.Delete(file);
            args = $"build --local --profile {profile} --platform android --non-interactive";
        }

        _logger.LogInformation("Running: eas {Args}", args);

        var result = await RunProcessAsync("eas", args, _settings.ProjectPath, ct);

        string? artifactPath = null;
        if (result.Success)
        {
            if (outputFile is not null)
            {
                // iOS: we know exactly where it landed
                artifactPath = File.Exists(outputFile) ? outputFile : null;
            }
            else
            {
                // Android: find whatever EAS produced
                artifactPath = Directory.GetFiles(outputDir, "*.apk")
                    .Concat(Directory.GetFiles(outputDir, "*.aab"))
                    .FirstOrDefault();
            }

            if (artifactPath is not null)
                _logger.LogInformation("Build artifact: {Path}", artifactPath);
            else
                _logger.LogWarning("Build succeeded but no artifact found in {Dir}", outputDir);
        }

        var combinedLogs = installResult.Logs + "\n--- EAS BUILD ---\n" + result.Logs;
        return new BuildResult(result.Success, combinedLogs, artifactPath, result.Error);
    }

    public async Task<BuildResult> SubmitAsync(string artifactPath, BuildPlatform platform, CancellationToken ct = default)
    {
        var platformArg = platform.ToString().ToLowerInvariant();
        var args = $"submit --platform {platformArg} --path \"{artifactPath}\" --non-interactive";
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
            psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin";

            using var process = Process.Start(psi);
            if (process is null)
                return (false, "", "Failed to start process");

            // Read stdout and stderr concurrently
            var stdoutTask = ReadStreamAsync(process.StandardOutput, logLines, "stdout", ct);
            var stderrTask = ReadStreamAsync(process.StandardError, logLines, "stderr", ct);

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

    private async Task ReadStreamAsync(StreamReader reader, LinkedList<string> logLines,
        string prefix, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
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
}
