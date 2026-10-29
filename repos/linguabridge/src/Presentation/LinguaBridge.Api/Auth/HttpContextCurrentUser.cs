using System.Security.Claims;
using LinguaBridge.Application.Abstractions;

namespace LinguaBridge.Api.Auth;

/// <summary>Reads the caller from the authenticated principal of the current request.</summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(ClaimNames.Subject), out var id) ? id : null;

    public string? ClientId => Principal?.FindFirstValue(ClaimNames.ClientId) ?? Principal?.FindFirstValue(ClaimNames.AuthorizedParty);

    public bool IsInRole(string role) =>
        Principal?.Claims.Any(c => c.Type == ClaimNames.Role && string.Equals(c.Value, role, StringComparison.Ordinal)) == true;
}

/// <summary>Claim types as the STS puts them in access tokens (OpenIddict does not remap them to the long WS-* names).</summary>
internal static class ClaimNames
{
    public const string Subject = "sub";
    public const string ClientId = "client_id";
    public const string AuthorizedParty = "azp";
    public const string Role = "role";
}
