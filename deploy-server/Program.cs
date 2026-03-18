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
    if (string.IsNullOrWhiteSpace(request.Profile))
        return Results.BadRequest(new { error = "Profile is required" });

    var job = await service.EnqueueAsync(request.Profile, request.Platform);
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

app.Run();
