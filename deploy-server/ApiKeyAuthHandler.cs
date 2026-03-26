using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DeployKit.DeployServer;

public class ApiKeyAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AuthSettings _authSettings;
    private readonly ApiKeyService _apiKeyService;

    public ApiKeyAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration,
        ApiKeyService apiKeyService) : base(options, logger, encoder)
    {
        _authSettings = configuration.GetSection("Auth").Get<AuthSettings>()
                        ?? throw new InvalidOperationException("Auth settings not configured");
        _apiKeyService = apiKeyService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-API-Key", out var providedKey) || string.IsNullOrEmpty(providedKey))
            return AuthenticateResult.Fail("Missing API key");

        var rawKey = providedKey.ToString();
        var providedBytes = Encoding.UTF8.GetBytes(rawKey);

        // 1. Check hardcoded admin key (constant-time)
        if (!string.IsNullOrEmpty(_authSettings.AdminApiKey))
        {
            var adminBytes = Encoding.UTF8.GetBytes(_authSettings.AdminApiKey);
            if (CryptographicOperations.FixedTimeEquals(providedBytes, adminBytes))
                return AuthenticateResult.Success(CreateTicketWithAllPermissions("admin", "Admin"));
        }

        // 2. Check hardcoded agent key (constant-time)
        if (!string.IsNullOrEmpty(_authSettings.AgentApiKey))
        {
            var agentBytes = Encoding.UTF8.GetBytes(_authSettings.AgentApiKey);
            if (CryptographicOperations.FixedTimeEquals(providedBytes, agentBytes))
                return AuthenticateResult.Success(CreateTicket("agent", "Agent", []));
        }

        // 3. Check SQLite-managed keys
        var record = await _apiKeyService.ValidateAsync(rawKey);
        if (record is not null)
            return AuthenticateResult.Success(CreateTicket(record.Name, null, record.Permissions));

        return AuthenticateResult.Fail("Invalid API key");
    }

    private AuthenticationTicket CreateTicketWithAllPermissions(string name, string role)
        => CreateTicket(name, role, Permissions.All);

    private AuthenticationTicket CreateTicket(string name, string? role, string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };

        if (role is not null)
            claims.Add(new Claim(ClaimTypes.Role, role));

        foreach (var permission in permissions)
            claims.Add(new Claim("permission", permission));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, Scheme.Name);
    }
}

public class AuthSettings
{
    public string AdminApiKey { get; set; } = string.Empty;
    public string AgentApiKey { get; set; } = string.Empty;
}
