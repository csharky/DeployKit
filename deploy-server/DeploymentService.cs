using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;

namespace DeployKit.DeployServer;

public class DeploymentService
{
    private const int MaxHistory = 20;
    private const int MaxAgentLogLines = 1000;

    private readonly DbConnectionFactory _db;
    private readonly JobQueue _queue;
    private readonly ProfileService _profileService;
    private readonly ILogger<DeploymentService> _logger;

    // In-memory heartbeat tracking: agentId → last seen UTC
    private readonly ConcurrentDictionary<string, DateTime> _heartbeats = new();

    public DeploymentService(
        DbConnectionFactory db,
        JobQueue queue,
        ProfileService profileService,
        ILogger<DeploymentService> logger)
    {
        _db = db;
        _queue = queue;
        _profileService = profileService;
        _logger = logger;
    }

    // ── Jobs ──────────────────────────────────────────────────────────────────

    public async Task<JobResponse?> EnqueueAsync(string profileId, EnvVar[]? envOverrides = null)
    {
        var profile = await _profileService.GetProfileAsync(profileId);
        if (profile is null) return null;

        // Merge overrides on top of profile env vars (overrides win on key match)
        var snapshot = profile;
        if (envOverrides is { Length: > 0 })
        {
            var dict = profile.EnvVars.ToDictionary(e => e.Key, e => e);
            foreach (var ov in envOverrides)
                dict[ov.Key] = ov;
            snapshot = profile with { EnvVars = dict.Values.ToArray() };
        }

        var jobId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        var job = new JobResponse(
            JobId: jobId,
            ProfileId: profileId,
            ProfileSnapshot: snapshot,
            Status: "pending",
            CreatedAt: now,
            StartedAt: null,
            CompletedAt: null,
            Logs: null,
            Error: null,
            ArtifactPath: null);

        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO jobs (id, profile_id, profile_snapshot, status, env_overrides, created_at, logs)
            VALUES (@id, @profileId, @profileSnapshot, 'pending', @envOverrides, @createdAt, '')
            """,
            new
            {
                id = jobId,
                profileId,
                profileSnapshot = JsonSerializer.Serialize(snapshot),
                envOverrides = envOverrides is { Length: > 0 } ? JsonSerializer.Serialize(envOverrides) : null,
                createdAt = now.ToString("O")
            });

        await _queue.EnqueueAsync(jobId);
        _logger.LogInformation("Enqueued job {JobId} profileId={ProfileId}", jobId, profileId);
        return job;
    }

    public async Task<JobResponse?> DequeueAsync(CancellationToken ct = default)
    {
        var jobId = _queue.TryDequeue();
        if (jobId is null) return null;

        await using var conn = _db.CreateConnection();
        var startedAt = DateTime.UtcNow;
        await conn.ExecuteAsync("""
            UPDATE jobs SET status = 'running', started_at = @startedAt WHERE id = @jobId
            """,
            new { jobId, startedAt = startedAt.ToString("O") });

        var job = await GetJobAsync(jobId);
        if (job is not null)
            _logger.LogInformation("Dequeued job {JobId}", jobId);

        return job;
    }

    public async Task<JobResponse?> UpdateStatusAsync(string jobId, string status, string? logs, string? error, string? artifactPath)
    {
        await using var conn = _db.CreateConnection();
        var completedAt = status is "completed" or "failed" ? DateTime.UtcNow.ToString("O") : null;

        await conn.ExecuteAsync("""
            UPDATE jobs SET
                status = @status,
                logs = COALESCE(@logs, logs),
                error = COALESCE(@error, error),
                artifact_path = COALESCE(@artifactPath, artifact_path),
                completed_at = COALESCE(@completedAt, completed_at)
            WHERE id = @jobId
            """,
            new { jobId, status, logs, error, artifactPath, completedAt });

        _logger.LogInformation("Updated job {JobId} status={Status}", jobId, status);
        return await GetJobAsync(jobId);
    }

    public async Task<JobResponse?> GetJobAsync(string jobId)
    {
        await using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync(
            "SELECT * FROM jobs WHERE id = @jobId", new { jobId });
        return row is null ? null : MapJob(row);
    }

    public async Task<List<JobResponse>> GetRecentAsync()
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync("""
            SELECT * FROM jobs
            WHERE status IN ('pending', 'running')
            UNION ALL
            SELECT * FROM (
                SELECT * FROM jobs
                WHERE status IN ('completed', 'failed', 'cancelled')
                ORDER BY completed_at DESC
                LIMIT 20
            )
            ORDER BY created_at DESC
            """);
        return rows.Select(MapJob).OrderByDescending(j => j.CreatedAt).ToList();
    }

    public async Task<bool> CancelAsync(string jobId)
    {
        var job = await GetJobAsync(jobId);
        if (job is null || job.Status != "pending") return false;

        _queue.MarkCancelled(jobId);

        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            UPDATE jobs SET status = 'cancelled', completed_at = @now WHERE id = @jobId
            """,
            new { jobId, now = DateTime.UtcNow.ToString("O") });

        _logger.LogInformation("Cancelled job {JobId}", jobId);
        return true;
    }

    public async Task<bool> HasActiveJobsForProfileAsync(string profileId)
    {
        await using var conn = _db.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM jobs
            WHERE profile_id = @profileId AND status IN ('pending', 'running')
            """,
            new { profileId });
        return count > 0;
    }

    // ── Agents ────────────────────────────────────────────────────────────────

    public async Task RecordHeartbeatAsync(string agentId)
    {
        var now = DateTime.UtcNow;
        _heartbeats[agentId] = now;

        await using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO agents (id, last_seen) VALUES (@agentId, @now)
            ON CONFLICT(id) DO UPDATE SET last_seen = @now
            """,
            new { agentId, now = now.ToString("O") });
    }

    public bool IsAgentAlive(string agentId)
    {
        return _heartbeats.TryGetValue(agentId, out var lastSeen)
               && DateTime.UtcNow - lastSeen < TimeSpan.FromMinutes(2);
    }

    // Async version for the /alive endpoint (also checks DB for agents not seen since startup)
    public async Task<bool> IsAgentAliveAsync(string agentId)
    {
        if (IsAgentAlive(agentId)) return true;

        await using var conn = _db.CreateConnection();
        var lastSeenStr = await conn.ExecuteScalarAsync<string?>(
            "SELECT last_seen FROM agents WHERE id = @agentId", new { agentId });

        if (lastSeenStr is null) return false;
        var lastSeen = DateTime.Parse(lastSeenStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
        return DateTime.UtcNow - lastSeen < TimeSpan.FromMinutes(2);
    }

    public async Task<List<AgentStatus>> GetAllAgentsAsync()
    {
        await using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync("SELECT id, last_seen FROM agents ORDER BY id");

        return rows.Select(row =>
        {
            var agentId = (string)row.id;
            var lastSeenStr = (string?)row.last_seen;
            DateTime? lastSeen = lastSeenStr is not null
                ? DateTime.Parse(lastSeenStr, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : null;
            var alive = IsAgentAlive(agentId);
            return new AgentStatus(agentId, alive, lastSeen);
        }).ToList();
    }

    public async Task StoreAgentLogsAsync(string agentId, string[] lines)
    {
        await using var conn = _db.CreateConnection();
        var existingJson = await conn.ExecuteScalarAsync<string?>(
            "SELECT logs FROM agents WHERE id = @agentId", new { agentId });

        var existing = existingJson is not null
            ? JsonSerializer.Deserialize<string[]>(existingJson) ?? []
            : Array.Empty<string>();

        var combined = existing.Concat(lines).TakeLast(MaxAgentLogLines).ToArray();
        var logsJson = JsonSerializer.Serialize(combined);

        await conn.ExecuteAsync("""
            INSERT INTO agents (id, logs) VALUES (@agentId, @logs)
            ON CONFLICT(id) DO UPDATE SET logs = @logs
            """,
            new { agentId, logs = logsJson });
    }

    public async Task<string[]> GetAgentLogsAsync(string agentId)
    {
        await using var conn = _db.CreateConnection();
        var json = await conn.ExecuteScalarAsync<string?>(
            "SELECT logs FROM agents WHERE id = @agentId", new { agentId });

        return json is not null
            ? JsonSerializer.Deserialize<string[]>(json) ?? []
            : [];
    }

    // ── Recovery ──────────────────────────────────────────────────────────────

    /// <summary>
    /// On startup, re-enqueue any pending jobs that were in the queue before a restart.
    /// </summary>
    public async Task RecoverPendingJobsAsync()
    {
        await using var conn = _db.CreateConnection();
        var pendingIds = await conn.QueryAsync<string>(
            "SELECT id FROM jobs WHERE status = 'pending' ORDER BY created_at ASC");

        var ids = pendingIds.ToList();
        if (ids.Count > 0)
        {
            await _queue.RecoverAsync(ids);
            _logger.LogInformation("Recovered {Count} pending jobs from DB", ids.Count);
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static JobResponse MapJob(dynamic row)
    {
        BuildProfile? snapshot = null;
        if (row.profile_snapshot is string snapshotJson && snapshotJson.Length > 0)
            snapshot = JsonSerializer.Deserialize<BuildProfile>(snapshotJson);

        return new JobResponse(
            JobId: (string)row.id,
            ProfileId: (string)row.profile_id,
            ProfileSnapshot: snapshot,
            Status: (string)row.status,
            CreatedAt: DateTime.Parse((string)row.created_at, null, System.Globalization.DateTimeStyles.RoundtripKind),
            StartedAt: row.started_at is string s && s.Length > 0
                ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
            CompletedAt: row.completed_at is string c && c.Length > 0
                ? DateTime.Parse(c, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
            Logs: row.logs is string l && l.Length > 0 ? l : null,
            Error: row.error is string e && e.Length > 0 ? e : null,
            ArtifactPath: row.artifact_path is string a && a.Length > 0 ? a : null);
    }
}
