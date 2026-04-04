using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace DeployKit.DeployAgent;

public class DeployServerClient
{
    private readonly HttpClient _http;
    private readonly DeployAgentSettings _settings;
    private readonly ILogger<DeployServerClient> _logger;

    public DeployServerClient(HttpClient http, IOptions<DeployAgentSettings> settings, ILogger<DeployServerClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;

        _http.BaseAddress = new Uri(_settings.ServerUrl.TrimEnd('/'));
        _http.DefaultRequestHeaders.Add("X-API-Key", _settings.AgentApiKey);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<JobResponse?> PollAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/api/agent/poll", null, ct);

            if (response.StatusCode == HttpStatusCode.NoContent)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JobResponse>(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to poll for jobs");
            return null;
        }
    }

    public async Task<bool> UpdateStatusAsync(string jobId, string status, string? logs = null, string? error = null, string? artifactPath = null, CancellationToken ct = default)
    {
        try
        {
            var request = new UpdateStatusRequest(status, logs, error, artifactPath);
            var response = await _http.PutAsJsonAsync($"/api/agent/status?jobId={jobId}", request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<StopResponse>(ct);
            return body?.StopRequested ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update job {JobId} status to {Status}", jobId, status);
            return false;
        }
    }

    private record StopResponse(bool StopRequested);

    public async Task HeartbeatAsync(CancellationToken ct = default)
    {
        try
        {
            var heartbeat = new AgentHeartbeat(_settings.AgentId, DateTime.UtcNow);
            var response = await _http.PostAsJsonAsync("/api/agent/heartbeat", heartbeat, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heartbeat failed");
        }
    }

    public async Task PushLogsAsync(string[] lines, CancellationToken ct = default)
    {
        try
        {
            var request = new AgentLogsRequest(_settings.AgentId, lines, DateTime.UtcNow);
            var response = await _http.PostAsJsonAsync("/api/agent/logs", request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Log push failed");
        }
    }
}
