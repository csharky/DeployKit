using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using PrepForge.DeployServer;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Redis
var redisConnection = builder.Configuration.GetConnectionString("Redis")
                      ?? throw new InvalidOperationException("Redis connection string not configured");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Auth
builder.Services.AddAuthentication("ApiKey")
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>("ApiKey", _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminPolicy", policy =>
    {
        policy.AddAuthenticationSchemes("ApiKey");
        policy.RequireRole("Admin");
    });
    options.AddPolicy("AgentPolicy", policy =>
    {
        policy.AddAuthenticationSchemes("ApiKey");
        policy.RequireRole("Agent");
    });
    options.AddPolicy("AnyAuthPolicy", policy =>
    {
        policy.AddAuthenticationSchemes("ApiKey");
        policy.RequireAuthenticatedUser();
    });
});

// Services
builder.Services.AddSingleton<DeploymentService>();
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Health
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Admin endpoints — trigger and monitor builds
var admin = app.MapGroup("/api/jobs")
    .RequireAuthorization("AdminPolicy");

admin.MapPost("/", async (CreateJobRequest request, DeploymentService service) =>
{
    if (string.IsNullOrWhiteSpace(request.ProfileId))
        return Results.BadRequest(new { error = "ProfileId is required" });

    var job = await service.EnqueueAsync(request.ProfileId);
    if (job is null)
        return Results.BadRequest(new { error = "Profile not found" });

    return Results.Created($"/api/jobs/{job.JobId}", job);
});

admin.MapGet("/", async (DeploymentService service) =>
{
    var jobs = await service.GetRecentAsync();
    return Results.Ok(jobs);
});

admin.MapGet("/{jobId}", async (string jobId, DeploymentService service) =>
{
    var job = await service.GetJobAsync(jobId);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

admin.MapDelete("/{jobId}", async (string jobId, DeploymentService service) =>
{
    var cancelled = await service.CancelAsync(jobId);
    return cancelled ? Results.Ok(new { message = "Job cancelled" }) : Results.NotFound();
});

// Admin: stream job log deltas via SSE
admin.MapGet("/{jobId}/stream", async (string jobId, HttpContext ctx, DeploymentService service, CancellationToken ct) =>
{
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    async Task Send(object payload)
    {
        var line = $"data: {JsonSerializer.Serialize(payload)}\n\n";
        await ctx.Response.WriteAsync(line, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    var job = await service.GetJobAsync(jobId);
    if (job is null) { await Send(new { type = "error", message = "not found" }); return; }

    int cursor = 0;
    string lastStatus = job.Status;

    try
    {
        while (!ct.IsCancellationRequested)
        {
            job = await service.GetJobAsync(jobId);
            if (job is null) break;

            var logs = job.Logs ?? "";
            if (logs.Length > cursor)
            {
                await Send(new { type = "log_delta", content = logs[cursor..] });
                cursor = logs.Length;
            }

            if (job.Status != lastStatus)
            {
                await Send(new { type = "status", status = job.Status, completedAt = job.CompletedAt });
                lastStatus = job.Status;
            }

            if (job.Status is "completed" or "failed" or "cancelled")
            {
                await Send(new { type = "done" });
                break;
            }

            await Task.Delay(2000, ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
});

// Admin endpoints — profile management
var profiles = app.MapGroup("/api/profiles")
    .RequireAuthorization("AdminPolicy");

profiles.MapPost("/", async (CreateProfileRequest req, ProfileService svc) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "Name is required" });
    if (req.Steps is null || req.Steps.Length == 0)
        return Results.BadRequest(new { error = "Steps are required" });

    var envVars = req.EnvVars ?? [];
    var dupKey = envVars.GroupBy(v => v.Key).FirstOrDefault(g => g.Count() > 1);
    if (dupKey is not null)
        return Results.BadRequest(new { error = $"Duplicate env var key: {dupKey.Key}" });

    if (await svc.IsNameTakenAsync(req.Name))
        return Results.Json(new { error = "A profile with that name already exists" }, statusCode: 409);

    var profile = await svc.CreateProfileAsync(
        req.Name,
        req.WorkingDirectory ?? string.Empty,
        envVars,
        req.Steps);
    return Results.Created($"/api/profiles/{profile.Id}", ProfileService.MaskSecrets(profile));
});

profiles.MapGet("/", async (ProfileService svc) =>
{
    var all = await svc.ListProfilesAsync();
    return Results.Ok(all.Select(ProfileService.MaskSecrets).ToList());
});

profiles.MapGet("/{id}", async (string id, ProfileService svc) =>
{
    var profile = await svc.GetProfileAsync(id);
    if (profile is null)
        return Results.NotFound(new { error = "Profile not found" });
    return Results.Ok(ProfileService.MaskSecrets(profile));
});

profiles.MapPut("/{id}", async (string id, UpdateProfileRequest req, ProfileService svc) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { error = "Name is required" });
    if (req.Steps is null || req.Steps.Length == 0)
        return Results.BadRequest(new { error = "Steps are required" });

    var dupKey = req.EnvVars.GroupBy(v => v.Key).FirstOrDefault(g => g.Count() > 1);
    if (dupKey is not null)
        return Results.BadRequest(new { error = $"Duplicate env var key: {dupKey.Key}" });

    if (await svc.IsNameTakenAsync(req.Name, excludeId: id))
        return Results.Json(new { error = "A profile with that name already exists" }, statusCode: 409);

    var updated = await svc.UpdateProfileAsync(id, req);
    if (updated is null)
        return Results.NotFound(new { error = "Profile not found" });
    return Results.Ok(ProfileService.MaskSecrets(updated));
});

profiles.MapDelete("/{id}", async (string id, ProfileService svc, DeploymentService deploySvc) =>
{
    if (await deploySvc.HasActiveJobsForProfileAsync(id))
        return Results.Json(new { error = "Profile has active jobs" }, statusCode: 409);

    var deleted = await svc.DeleteProfileAsync(id);
    if (!deleted)
        return Results.NotFound(new { error = "Profile not found" });
    return Results.Ok(new { });
});

// Admin: get status of all known agents
app.MapGet("/api/agents", async (DeploymentService service) =>
{
    var agents = await service.GetAllAgentsAsync();
    return Results.Ok(agents);
}).RequireAuthorization("AdminPolicy");

// Agent endpoints — polling and status reporting
var agent = app.MapGroup("/api/agent")
    .RequireAuthorization("AgentPolicy");

agent.MapPost("/poll", async (DeploymentService service) =>
{
    var job = await service.DequeueAsync();
    return job is not null ? Results.Ok(job) : Results.NoContent();
});

agent.MapPut("/status", async (UpdateStatusRequest request, DeploymentService service, HttpContext context) =>
{
    // jobId comes as query param
    var jobId = context.Request.Query["jobId"].ToString();
    if (string.IsNullOrEmpty(jobId))
        return Results.BadRequest(new { error = "jobId query parameter is required" });

    var job = await service.UpdateStatusAsync(jobId, request.Status, request.Logs, request.Error, request.ArtifactPath);
    return job is not null ? Results.Ok(job) : Results.NotFound();
});

agent.MapPost("/heartbeat", async (AgentHeartbeat heartbeat, DeploymentService service) =>
{
    await service.RecordHeartbeatAsync(heartbeat.AgentId);
    return Results.Ok(new { message = "OK" });
});

agent.MapPost("/logs", async (AgentLogsRequest request, DeploymentService service) =>
{
    await service.StoreAgentLogsAsync(request.AgentId, request.Lines);
    return Results.Ok(new { message = "OK" });
});

// Status endpoint — accessible by both admin and agent
app.MapGet("/api/agent/alive/{agentId}", async (string agentId, DeploymentService service) =>
{
    var alive = await service.IsAgentAliveAsync(agentId);
    return Results.Ok(new { agentId, alive });
}).RequireAuthorization("AnyAuthPolicy");

// Admin: read agent logs
app.MapGet("/api/agent/{agentId}/logs", async (string agentId, int? lines, DeploymentService service) =>
{
    var allLines = await service.GetAgentLogsAsync(agentId);
    var result = lines.HasValue ? allLines.TakeLast(lines.Value).ToArray() : allLines;
    return Results.Ok(new { agentId, lines = result });
}).RequireAuthorization("AdminPolicy");

// Admin: stream agent log lines via SSE
app.MapGet("/api/agent/{agentId}/logs/stream", async (string agentId, int? from, HttpContext ctx, DeploymentService service, CancellationToken ct) =>
{
    ctx.Response.Headers["Content-Type"] = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    async Task Send(object payload)
    {
        var line = $"data: {JsonSerializer.Serialize(payload)}\n\n";
        await ctx.Response.WriteAsync(line, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    int cursor = from ?? 0;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var lines = await service.GetAgentLogsAsync(agentId);
            if (lines.Length > cursor)
            {
                await Send(new { type = "lines", lines = lines[cursor..] });
                cursor = lines.Length;
            }
            await Task.Delay(3000, ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
}).RequireAuthorization("AdminPolicy");

app.Run();

// Make Program accessible to WebApplicationFactory in test project
public partial class Program { }
