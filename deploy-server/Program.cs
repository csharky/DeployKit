using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using DeployKit.DeployServer;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

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
    static void AddPermissionPolicy(AuthorizationOptions opts, string name, string permission) =>
        opts.AddPolicy(name, policy =>
        {
            policy.AddAuthenticationSchemes("ApiKey");
            policy.RequireAssertion(ctx =>
                ctx.User.IsInRole("Admin") ||
                ctx.User.HasClaim("permission", permission));
        });

    AddPermissionPolicy(options, "JobsRun", Permissions.JobsRun);
    AddPermissionPolicy(options, "JobsRead", Permissions.JobsRead);
    AddPermissionPolicy(options, "ProfilesRead", Permissions.ProfilesRead);
    AddPermissionPolicy(options, "ProfilesWrite", Permissions.ProfilesWrite);
    AddPermissionPolicy(options, "ApiKeysManage", Permissions.ApiKeysManage);

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
builder.Services.AddSingleton<DbConnectionFactory>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddSingleton<DeploymentService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

// Initialize DB and recover pending jobs
var db = app.Services.GetRequiredService<DbConnectionFactory>();
await db.InitializeAsync();

var deploymentService = app.Services.GetRequiredService<DeploymentService>();
await deploymentService.RecoverPendingJobsAsync();

app.MapOpenApi();
app.MapScalarApiReference();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Health
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// ── Jobs ──────────────────────────────────────────────────────────────────────

app.MapPost("/api/jobs", async (CreateJobRequest request, DeploymentService service, ProfileService profileSvc, AuditService audit, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(request.ProfileId))
        return Results.BadRequest(new { error = "ProfileId is required" });

    if (request.EnvOverrides is { Length: > 0 })
    {
        var dupKey = request.EnvOverrides.GroupBy(v => v.Key).FirstOrDefault(g => g.Count() > 1);
        if (dupKey is not null)
            return Results.BadRequest(new { error = $"Duplicate env override key: {dupKey.Key}" });

        var profileForLockCheck = await profileSvc.GetProfileAsync(request.ProfileId);
        if (profileForLockCheck is not null)
        {
            var lockedKeys = profileForLockCheck.EnvVars.Where(v => v.IsLocked).Select(v => v.Key).ToHashSet();
            var lockedOverride = request.EnvOverrides.FirstOrDefault(ov => lockedKeys.Contains(ov.Key));
            if (lockedOverride is not null)
                return Results.BadRequest(new { error = $"Env var '{lockedOverride.Key}' is locked and cannot be overridden" });
        }
    }

    var job = await service.EnqueueAsync(request.ProfileId, request.EnvOverrides);
    if (job is null)
        return Results.BadRequest(new { error = "Profile not found" });

    await audit.LogAsync(ctx.User.Identity?.Name ?? "unknown", "job.created",
        JsonSerializer.Serialize(new { job.JobId, job.ProfileId }));

    return Results.Created($"/api/jobs/{job.JobId}", job);
}).RequireAuthorization("JobsRun");

app.MapGet("/api/jobs", async (DeploymentService service) =>
{
    var jobs = await service.GetRecentAsync();
    return Results.Ok(jobs);
}).RequireAuthorization("JobsRead");

app.MapGet("/api/jobs/{jobId}", async (string jobId, DeploymentService service) =>
{
    var job = await service.GetJobAsync(jobId);
    return job is not null ? Results.Ok(job) : Results.NotFound();
}).RequireAuthorization("JobsRead");

app.MapDelete("/api/jobs/{jobId}", async (string jobId, DeploymentService service, AuditService audit, HttpContext ctx) =>
{
    var cancelled = await service.CancelAsync(jobId);
    if (!cancelled) return Results.NotFound();

    await audit.LogAsync(ctx.User.Identity?.Name ?? "unknown", "job.cancelled",
        JsonSerializer.Serialize(new { jobId }));

    return Results.Ok(new { message = "Job cancelled" });
}).RequireAuthorization("JobsRun");

// SSE: stream job log deltas
app.MapGet("/api/jobs/{jobId}/stream", async (string jobId, HttpContext ctx, DeploymentService service, CancellationToken ct) =>
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
}).RequireAuthorization("JobsRead");

// ── Profiles ──────────────────────────────────────────────────────────────────

app.MapPost("/api/profiles", async (CreateProfileRequest req, ProfileService svc, AuditService audit, HttpContext ctx) =>
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
        req.Name, req.WorkingDirectory ?? string.Empty, envVars, req.Steps);

    await audit.LogAsync(ctx.User.Identity?.Name ?? "unknown", "profile.created",
        JsonSerializer.Serialize(new { profile.Id, profile.Name }));

    return Results.Created($"/api/profiles/{profile.Id}", ProfileService.MaskSecrets(profile));
}).RequireAuthorization("ProfilesWrite");

app.MapGet("/api/profiles", async (ProfileService svc) =>
{
    var all = await svc.ListProfilesAsync();
    return Results.Ok(all.Select(ProfileService.MaskSecrets).ToList());
}).RequireAuthorization("ProfilesRead");

app.MapGet("/api/profiles/{id}", async (string id, ProfileService svc) =>
{
    var profile = await svc.GetProfileAsync(id);
    if (profile is null)
        return Results.NotFound(new { error = "Profile not found" });
    return Results.Ok(ProfileService.MaskSecrets(profile));
}).RequireAuthorization("ProfilesRead");

app.MapPut("/api/profiles/{id}", async (string id, UpdateProfileRequest req, ProfileService svc, AuditService audit, HttpContext ctx) =>
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

    await audit.LogAsync(ctx.User.Identity?.Name ?? "unknown", "profile.updated",
        JsonSerializer.Serialize(new { updated.Id, updated.Name }));

    return Results.Ok(ProfileService.MaskSecrets(updated));
}).RequireAuthorization("ProfilesWrite");

app.MapDelete("/api/profiles/{id}", async (string id, ProfileService svc, DeploymentService deploySvc, AuditService audit, HttpContext ctx) =>
{
    if (await deploySvc.HasActiveJobsForProfileAsync(id))
        return Results.Json(new { error = "Profile has active jobs" }, statusCode: 409);

    var profile = await svc.GetProfileAsync(id);
    var deleted = await svc.DeleteProfileAsync(id);
    if (!deleted)
        return Results.NotFound(new { error = "Profile not found" });

    await audit.LogAsync(ctx.User.Identity?.Name ?? "unknown", "profile.deleted",
        JsonSerializer.Serialize(new { id, name = profile?.Name }));

    return Results.Ok(new { });
}).RequireAuthorization("ProfilesWrite");

// ── Current user ──────────────────────────────────────────────────────────────

app.MapGet("/api/me", (HttpContext ctx) =>
{
    var name = ctx.User.Identity?.Name ?? "unknown";
    var isAdmin = ctx.User.IsInRole("Admin");
    var permissions = isAdmin
        ? Permissions.All
        : ctx.User.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToArray();
    return Results.Ok(new { name, permissions });
}).RequireAuthorization("AnyAuthPolicy");

// ── Agents (admin) ────────────────────────────────────────────────────────────

app.MapGet("/api/agents", async (DeploymentService service) =>
{
    var agents = await service.GetAllAgentsAsync();
    return Results.Ok(agents);
}).RequireAuthorization("JobsRead");

app.MapGet("/api/agent/{agentId}/logs", async (string agentId, int? lines, DeploymentService service) =>
{
    var allLines = await service.GetAgentLogsAsync(agentId);
    var result = lines.HasValue ? allLines.TakeLast(lines.Value).ToArray() : allLines;
    return Results.Ok(new { agentId, lines = result });
}).RequireAuthorization("JobsRead");

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
}).RequireAuthorization("JobsRead");

app.MapGet("/api/agent/alive/{agentId}", async (string agentId, DeploymentService service) =>
{
    var alive = await service.IsAgentAliveAsync(agentId);
    return Results.Ok(new { agentId, alive });
}).RequireAuthorization("AnyAuthPolicy");

// ── Agent endpoints ───────────────────────────────────────────────────────────

var agent = app.MapGroup("/api/agent")
    .RequireAuthorization("AgentPolicy");

agent.MapPost("/poll", async (DeploymentService service) =>
{
    var job = await service.DequeueAsync();
    return job is not null ? Results.Ok(job) : Results.NoContent();
});

agent.MapPut("/status", async (UpdateStatusRequest request, DeploymentService service, HttpContext context) =>
{
    var jobId = context.Request.Query["jobId"].ToString();
    if (string.IsNullOrEmpty(jobId))
        return Results.BadRequest(new { error = "jobId query parameter is required" });

    var job = await service.UpdateStatusAsync(jobId, request.Status, request.Logs, request.Error, request.ArtifactPath);
    if (job is null) return Results.NotFound();
    return Results.Ok(new { stopRequested = job.Status == "cancelled" });
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

// ── API Keys ──────────────────────────────────────────────────────────────────

app.MapGet("/api/apikeys", async (ApiKeyService apiKeyService) =>
{
    var keys = await apiKeyService.ListAsync();
    return Results.Ok(keys);
}).RequireAuthorization("ApiKeysManage");

app.MapPost("/api/apikeys", async (CreateApiKeyRequest request, ApiKeyService apiKeyService, AuditService audit, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "Name is required" });

    if (request.Permissions is null || request.Permissions.Length == 0)
        return Results.BadRequest(new { error = "At least one permission is required" });

    var invalid = request.Permissions.Except(Permissions.All).ToArray();
    if (invalid.Length > 0)
        return Results.BadRequest(new { error = $"Invalid permissions: {string.Join(", ", invalid)}" });

    var created = await apiKeyService.CreateAsync(request.Name, request.Permissions);

    await audit.LogAsync(ctx.User.Identity?.Name ?? "unknown", "apikey.created",
        JsonSerializer.Serialize(new { created.Id, created.Name, created.Permissions }));

    return Results.Created($"/api/apikeys/{created.Id}", created);
}).RequireAuthorization("ApiKeysManage");

app.MapDelete("/api/apikeys/{id}", async (string id, ApiKeyService apiKeyService, AuditService audit, HttpContext ctx) =>
{
    var keys = await apiKeyService.ListAsync();
    var key = keys.FirstOrDefault(k => k.Id == id);

    var revoked = await apiKeyService.RevokeAsync(id);
    if (!revoked) return Results.NotFound(new { error = "API key not found or already revoked" });

    await audit.LogAsync(ctx.User.Identity?.Name ?? "unknown", "apikey.revoked",
        JsonSerializer.Serialize(new { id, name = key?.Name }));

    return Results.Ok(new { message = "API key revoked" });
}).RequireAuthorization("ApiKeysManage");

// ── Audit Log ─────────────────────────────────────────────────────────────────

app.MapGet("/api/audit", async (int? count, AuditService audit) =>
{
    var entries = await audit.GetRecentAsync(count ?? 100);
    return Results.Ok(entries);
}).RequireAuthorization("ApiKeysManage");

app.Run();

// Make Program accessible to WebApplicationFactory in test project
public partial class Program { }
