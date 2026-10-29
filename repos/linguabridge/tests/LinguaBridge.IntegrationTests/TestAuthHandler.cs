using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaBridge.IntegrationTests;

/// <summary>
/// Stands in for the OpenIddict validation handler. It authenticates only requests carrying <c>X-Test-User</c>
/// (the value becomes <c>sub</c>, verbatim), so one factory can exercise both 401 and 200.
/// </summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";
    public const string ClientHeader = "X-Test-Client";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("sub", user.ToString()) };
        if (Request.Headers.TryGetValue(RolesHeader, out var roles))
            claims.AddRange(roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => new Claim("role", r)));
        if (Request.Headers.TryGetValue(ClientHeader, out var client))
            claims.Add(new Claim("client_id", client.ToString()));

        var identity = new ClaimsIdentity(claims, SchemeName, nameType: "name", roleType: "role");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
