using System.Text.Json.Serialization;

namespace PrepForge.DeployServer;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildPlatform { Ios }

public record CreateJobRequest(string Profile, BuildPlatform Platform);

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

public record AgentStatus(string AgentId, bool Alive, DateTime? LastSeen);

public record AgentHeartbeat(string AgentId, DateTime Timestamp);

public record AgentLogsRequest(string AgentId, string[] Lines, DateTime Timestamp);
