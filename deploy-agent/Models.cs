namespace DeployKit.DeployAgent;

public record EnvVar(string Key, string Value, bool IsSecret);

public record ProfileSnapshot(
    string Id,
    string Name,
    string WorkingDirectory,
    EnvVar[] EnvVars,
    string[] Steps);

public record JobResponse(
    string JobId,
    string ProfileId,
    ProfileSnapshot? ProfileSnapshot,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? Logs,
    string? Error,
    string? ArtifactPath);

public record UpdateStatusRequest(string Status, string? Logs, string? Error, string? ArtifactPath);

public record AgentHeartbeat(string AgentId, DateTime Timestamp);

public record AgentLogsRequest(string AgentId, string[] Lines, DateTime Timestamp);

public class DeployAgentSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    public string AgentApiKey { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 10;
    public string AgentId { get; set; } = Environment.MachineName;
    public string LogFilePath { get; set; } = "/tmp/deploykit-deploy-agent.log";
    public int LogPushIntervalSeconds { get; set; } = 30;
}
