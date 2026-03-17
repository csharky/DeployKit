using Microsoft.Extensions.Options;

namespace PrepForge.DeployAgent;

public class Worker : BackgroundService
{
    private readonly DeployServerClient _client;
    private readonly EasBuildRunner _runner;
    private readonly DeployAgentSettings _settings;
    private readonly ILogger<Worker> _logger;
    private DateTime _lastLogPush = DateTime.MinValue;
    private long _lastLogFilePosition = 0;

    public Worker(
        DeployServerClient client,
        EasBuildRunner runner,
        IOptions<DeployAgentSettings> settings,
        ILogger<Worker> logger)
    {
        _client = client;
        _runner = runner;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Deploy agent started. Polling {Url} every {Interval}s",
            _settings.ServerUrl, _settings.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _client.HeartbeatAsync(stoppingToken);
                await MaybePushLogsAsync(stoppingToken);

                var job = await _client.PollAsync(stoppingToken);
                if (job is not null)
                {
                    await ProcessJobAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in polling loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("Deploy agent stopped");
    }

    private async Task MaybePushLogsAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _lastLogPush).TotalSeconds < _settings.LogPushIntervalSeconds)
            return;

        if (!File.Exists(_settings.LogFilePath))
            return;

        try
        {
            string[] newLines;
            using (var stream = new FileStream(_settings.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(_lastLogFilePosition, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(ct);
                _lastLogFilePosition = stream.Position;
                newLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }

            if (newLines.Length > 0)
                await _client.PushLogsAsync(newLines, ct);

            _lastLogPush = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to push logs");
        }
    }

    private async Task ProcessJobAsync(JobResponse job, CancellationToken ct)
    {
        _logger.LogInformation("Processing job {JobId}: profile={Profile} platform={Platform}",
            job.JobId, job.Profile, job.Platform);

        await _client.UpdateStatusAsync(job.JobId, "running", ct: ct);

        // Step 1: Build
        var buildResult = await _runner.BuildAsync(job.Profile, job.Platform, ct);

        if (!buildResult.Success)
        {
            _logger.LogWarning("Build failed for job {JobId}: {Error}", job.JobId, buildResult.Error);
            await _client.UpdateStatusAsync(job.JobId, "failed", buildResult.Logs, buildResult.Error, ct: ct);
            return;
        }

        _logger.LogInformation("Build succeeded for job {JobId}", job.JobId);

        // Step 2: Submit to TestFlight (if we have an IPA)
        if (buildResult.ArtifactPath is not null && buildResult.ArtifactPath.EndsWith(".ipa"))
        {
            _logger.LogInformation("Submitting to TestFlight: {Path}", buildResult.ArtifactPath);
            var submitResult = await _runner.SubmitAsync(buildResult.ArtifactPath, job.Platform, ct);

            if (!submitResult.Success)
            {
                _logger.LogWarning("Submit failed for job {JobId}: {Error}", job.JobId, submitResult.Error);
                var combinedLogs = buildResult.Logs + "\n--- SUBMIT ---\n" + submitResult.Logs;
                await _client.UpdateStatusAsync(job.JobId, "failed", combinedLogs, $"Submit failed: {submitResult.Error}", ct: ct);
                return;
            }

            var allLogs = buildResult.Logs + "\n--- SUBMIT ---\n" + submitResult.Logs;
            await _client.UpdateStatusAsync(job.JobId, "completed", allLogs, artifactPath: buildResult.ArtifactPath, ct: ct);
        }
        else
        {
            await _client.UpdateStatusAsync(job.JobId, "completed", buildResult.Logs, artifactPath: buildResult.ArtifactPath, ct: ct);
        }

        _logger.LogInformation("Job {JobId} completed successfully", job.JobId);
    }
}
