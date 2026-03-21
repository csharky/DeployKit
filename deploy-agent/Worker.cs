using System.Text;
using Microsoft.Extensions.Options;

namespace DeployKit.DeployAgent;

public class Worker : BackgroundService
{
    private readonly DeployServerClient _client;
    private readonly StepRunner _runner;
    private readonly DeployAgentSettings _settings;
    private readonly ILogger<Worker> _logger;
    private DateTime _lastLogPush = DateTime.MinValue;
    private long _lastLogFilePosition = 0;

    public Worker(
        DeployServerClient client,
        StepRunner runner,
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
        if (job.ProfileSnapshot is null)
        {
            _logger.LogWarning("Job {JobId} has no profile snapshot — cannot execute", job.JobId);
            await _client.UpdateStatusAsync(job.JobId, "failed", null,
                "Job has no profile snapshot — cannot execute steps", ct: ct);
            return;
        }

        _logger.LogInformation("Processing job {JobId}: profile={Profile}",
            job.JobId, job.ProfileSnapshot.Name);

        await _client.UpdateStatusAsync(job.JobId, "running", ct: ct);

        // Set up incremental log streaming
        var logBuffer = new StringBuilder();
        var logLock = new object();

        _runner.SetLogCallback(line =>
        {
            lock (logLock)
            {
                if (logBuffer.Length > 0) logBuffer.Append('\n');
                logBuffer.Append(line);
            }
        });

        using var logCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var logPushTask = Task.Run(async () =>
        {
            try
            {
                while (!logCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(5000, logCts.Token);
                    string snapshot;
                    lock (logLock)
                    {
                        snapshot = logBuffer.ToString();
                    }
                    if (snapshot.Length > 0)
                    {
                        await _client.UpdateStatusAsync(job.JobId, "running", snapshot, ct: logCts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { /* expected */ }
        }, logCts.Token);

        try
        {
            var result = await _runner.RunStepsAsync(job.ProfileSnapshot, ct);

            string logs;
            lock (logLock)
            {
                logs = logBuffer.ToString();
            }

            if (!result.Success)
            {
                _logger.LogWarning("Job {JobId} failed: {Error}", job.JobId, result.Error);
                await _client.UpdateStatusAsync(job.JobId, "failed", logs, result.Error, ct: ct);
                return;
            }

            await _client.UpdateStatusAsync(job.JobId, "completed", logs, artifactPath: null, ct: ct);
            _logger.LogInformation("Job {JobId} completed successfully", job.JobId);
        }
        finally
        {
            logCts.Cancel();
            _runner.SetLogCallback(null);
            try { await logPushTask; } catch (OperationCanceledException) { }
        }
    }
}
