using System.Text.Json;
using StackExchange.Redis;

namespace PrepForge.DeployServer;

public class DeploymentService
{
    private const string QueueKey = "deploy:queue";
    private const string RunningKey = "deploy:running";
    private const string HistoryKey = "deploy:history";
    private const int MaxHistory = 20;
    private static readonly TimeSpan JobTtl = TimeSpan.FromDays(7);

    private readonly IDatabase _redis;
    private readonly ILogger<DeploymentService> _logger;

    public DeploymentService(IConnectionMultiplexer redis, ILogger<DeploymentService> logger)
    {
        _redis = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<JobResponse> EnqueueAsync(string profile, BuildPlatform platform)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        var job = new JobResponse(
            JobId: jobId,
            Profile: profile,
            Platform: platform,
            Status: "pending",
            CreatedAt: now,
            StartedAt: null,
            CompletedAt: null,
            Logs: null,
            Error: null,
            ArtifactPath: null);

        await SaveJobAsync(job);
        await _redis.ListLeftPushAsync(QueueKey, jobId);

        _logger.LogInformation("Enqueued job {JobId} profile={Profile} platform={Platform}", jobId, profile, platform);
        return job;
    }

    public async Task<JobResponse?> DequeueAsync()
    {
        var jobId = await _redis.ListRightPopAsync(QueueKey);
        if (jobId.IsNullOrEmpty)
            return null;

        var job = await GetJobAsync(jobId.ToString());
        if (job is null)
            return null;

        var updated = job with
        {
            Status = "running",
            StartedAt = DateTime.UtcNow
        };

        await SaveJobAsync(updated);
        await _redis.SetAddAsync(RunningKey, updated.JobId);
        _logger.LogInformation("Dequeued job {JobId}", updated.JobId);
        return updated;
    }

    public async Task<JobResponse?> UpdateStatusAsync(string jobId, string status, string? logs, string? error, string? artifactPath)
    {
        var job = await GetJobAsync(jobId);
        if (job is null)
            return null;

        var updated = job with
        {
            Status = status,
            Logs = logs ?? job.Logs,
            Error = error ?? job.Error,
            ArtifactPath = artifactPath ?? job.ArtifactPath,
            CompletedAt = status is "completed" or "failed" ? DateTime.UtcNow : job.CompletedAt
        };

        await SaveJobAsync(updated);

        if (status is "completed" or "failed" or "cancelled")
        {
            await _redis.SetRemoveAsync(RunningKey, jobId);
            await _redis.ListLeftPushAsync(HistoryKey, jobId);
            await _redis.ListTrimAsync(HistoryKey, 0, MaxHistory - 1);
        }

        _logger.LogInformation("Updated job {JobId} status={Status}", jobId, status);
        return updated;
    }

    public async Task<JobResponse?> GetJobAsync(string jobId)
    {
        var json = await _redis.StringGetAsync(JobKey(jobId));
        if (json.IsNullOrEmpty)
            return null;

        return JsonSerializer.Deserialize<JobResponse>(json.ToString());
    }

    public async Task<List<JobResponse>> GetRecentAsync()
    {
        var jobs = new List<JobResponse>();

        // Get queued jobs
        var queuedIds = await _redis.ListRangeAsync(QueueKey);
        foreach (var id in queuedIds)
        {
            var job = await GetJobAsync(id.ToString());
            if (job is not null)
                jobs.Add(job);
        }

        // Get running jobs
        var runningIds = await _redis.SetMembersAsync(RunningKey);
        foreach (var id in runningIds)
        {
            var job = await GetJobAsync(id.ToString());
            if (job is not null && jobs.All(j => j.JobId != job.JobId))
                jobs.Add(job);
        }

        // Get history
        var historyIds = await _redis.ListRangeAsync(HistoryKey);
        foreach (var id in historyIds)
        {
            var job = await GetJobAsync(id.ToString());
            if (job is not null && jobs.All(j => j.JobId != job.JobId))
                jobs.Add(job);
        }

        return jobs.OrderByDescending(j => j.CreatedAt).ToList();
    }

    public async Task<bool> CancelAsync(string jobId)
    {
        var job = await GetJobAsync(jobId);
        if (job is null || job.Status != "pending")
            return false;

        await _redis.ListRemoveAsync(QueueKey, jobId);
        var updated = job with { Status = "cancelled", CompletedAt = DateTime.UtcNow };
        await SaveJobAsync(updated);

        await _redis.ListLeftPushAsync(HistoryKey, jobId);
        await _redis.ListTrimAsync(HistoryKey, 0, MaxHistory - 1);

        _logger.LogInformation("Cancelled job {JobId}", jobId);
        return true;
    }

    public async Task RecordHeartbeatAsync(string agentId)
    {
        var now = DateTime.UtcNow;
        await _redis.StringSetAsync($"deploy:agent:{agentId}:heartbeat", now.ToString("O"), TimeSpan.FromMinutes(2));
        await _redis.SetAddAsync("deploy:agents", agentId);
    }

    public async Task<bool> IsAgentAliveAsync(string agentId)
    {
        return await _redis.KeyExistsAsync($"deploy:agent:{agentId}:heartbeat");
    }

    public async Task<List<AgentStatus>> GetAllAgentsAsync()
    {
        var agentIds = await _redis.SetMembersAsync("deploy:agents");
        var result = new List<AgentStatus>();

        foreach (var id in agentIds)
        {
            var agentId = id.ToString();
            var heartbeatJson = await _redis.StringGetAsync($"deploy:agent:{agentId}:heartbeat");
            var alive = !heartbeatJson.IsNullOrEmpty;
            var lastSeen = alive ? DateTime.Parse(heartbeatJson.ToString()) : (DateTime?)null;
            result.Add(new AgentStatus(agentId, alive, lastSeen));
        }

        return result.OrderBy(a => a.AgentId).ToList();
    }

    public async Task StoreAgentLogsAsync(string agentId, string[] lines)
    {
        const int MaxLines = 1000;
        var key = $"deploy:agent:{agentId}:logs";

        var existing = await _redis.StringGetAsync(key);
        var stored = existing.IsNullOrEmpty
            ? []
            : JsonSerializer.Deserialize<string[]>(existing.ToString()) ?? [];

        var combined = stored.Concat(lines).TakeLast(MaxLines).ToArray();
        await _redis.StringSetAsync(key, JsonSerializer.Serialize(combined), TimeSpan.FromHours(1));
    }

    public async Task<string[]> GetAgentLogsAsync(string agentId)
    {
        var key = $"deploy:agent:{agentId}:logs";
        var json = await _redis.StringGetAsync(key);
        if (json.IsNullOrEmpty)
            return [];

        return JsonSerializer.Deserialize<string[]>(json.ToString()) ?? [];
    }

    private async Task SaveJobAsync(JobResponse job)
    {
        var json = JsonSerializer.Serialize(job);
        await _redis.StringSetAsync(JobKey(job.JobId), json, JobTtl);
    }

    private static string JobKey(string jobId) => $"deploy:job:{jobId}";
}
