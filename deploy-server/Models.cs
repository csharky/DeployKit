namespace DeployKit.DeployServer;

public record CreateJobRequest(string ProfileId, EnvVar[]? EnvOverrides = null);

public record JobResponse(
    string JobId,
    string ProfileId,
    BuildProfile? ProfileSnapshot,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Logs,
    string? Error,
    string? ArtifactPath);

public record UpdateStatusRequest(string Status, string? Logs, string? Error, string? ArtifactPath);

public record AgentStatus(string AgentId, bool Alive, DateTime? LastSeen);

public record AgentHeartbeat(string AgentId, DateTime Timestamp);

public record AgentLogsRequest(string AgentId, string[] Lines, DateTime Timestamp);

public record EnvVar(string Key, string Value, bool IsSecret);

public record BuildProfile(
    string Id,
    string Name,
    string WorkingDirectory,
    EnvVar[] EnvVars,
    string[] Steps);

public record CreateProfileRequest(
    string Name,
    string? WorkingDirectory,
    EnvVar[]? EnvVars,
    string[] Steps);

public record UpdateProfileRequest(
    string Name,
    string WorkingDirectory,
    EnvVar[] EnvVars,
    string[] Steps);

public record ProfileResponse(
    string Id,
    string Name,
    string WorkingDirectory,
    EnvVar[] EnvVars,
    string[] Steps);

// ── API Keys ──────────────────────────────────────────────────────────────────

public record ApiKeyRecord(
    string Id,
    string Name,
    string KeyHash,
    string KeyPrefix,
    string[] Permissions,
    DateTime CreatedAt,
    bool Revoked);

public record ApiKeyCreatedResponse(
    string Id,
    string Name,
    string Key,
    string KeyPrefix,
    string[] Permissions,
    DateTime CreatedAt);

public record ApiKeyListItem(
    string Id,
    string Name,
    string KeyPrefix,
    string[] Permissions,
    DateTime CreatedAt,
    bool Revoked);

public record CreateApiKeyRequest(string Name, string[] Permissions);

// ── Audit ─────────────────────────────────────────────────────────────────────

public record AuditEntry(
    string Id,
    string KeyName,
    string Action,
    DateTime Timestamp,
    string? Details);
