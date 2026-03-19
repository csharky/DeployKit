using System.Text.Json.Serialization;

namespace PrepForge.DeployAgent;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildPlatform { Ios, Android }

public record JobResponse(
    string JobId,
    string Profile,
    BuildPlatform Platform,
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
    public string KeychainPassword { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 10;
    public string ProjectPath { get; set; } = string.Empty;
    public string BuildOutputPath { get; set; } = "./build-output";
    public string AgentId { get; set; } = Environment.MachineName;
    public string LogFilePath { get; set; } = "/tmp/prepforge-deploy-agent.log";
    public int LogPushIntervalSeconds { get; set; } = 30;
}
