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

    public ApiKeyAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration) : base(options, logger, encoder)
    {
        _authSettings = configuration.GetSection("Auth").Get<AuthSettings>()
                        ?? throw new InvalidOperationException("Auth settings not configured");
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-API-Key", out var providedKey) || string.IsNullOrEmpty(providedKey))
            return Task.FromResult(AuthenticateResult.Fail("Missing API key"));

        var providedBytes = Encoding.UTF8.GetBytes(providedKey.ToString());

        // Check admin key
        if (!string.IsNullOrEmpty(_authSettings.AdminApiKey))
        {
            var adminBytes = Encoding.UTF8.GetBytes(_authSettings.AdminApiKey);
            if (CryptographicOperations.FixedTimeEquals(providedBytes, adminBytes))
                return Task.FromResult(AuthenticateResult.Success(CreateTicket("admin", "Admin")));
        }

        // Check agent key
        if (!string.IsNullOrEmpty(_authSettings.AgentApiKey))
        {
            var agentBytes = Encoding.UTF8.GetBytes(_authSettings.AgentApiKey);
            if (CryptographicOperations.FixedTimeEquals(providedBytes, agentBytes))
                return Task.FromResult(AuthenticateResult.Success(CreateTicket("agent", "Agent")));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
    }

    private AuthenticationTicket CreateTicket(string name, string role)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role)
        };
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
