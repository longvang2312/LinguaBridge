using System.Security.Claims;
using AppInHouse.Identity.Application.Admin.Security;

namespace AppInHouse.Identity.Admin.Api.Infrastructure;

/// <summary>The caller of the current HTTP request, taken from the validated access token.</summary>
public sealed class HttpCurrentCaller : ICurrentCaller
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentCaller(IHttpContextAccessor accessor) => _accessor = accessor;

    public string? UserId
    {
        get
        {
            var user = _accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
                return null;

            return user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        }
    }

    /// <remarks>
    /// The socket address. Behind a reverse proxy the host must enable the forwarded-headers middleware with its known
    /// proxies; this class never trusts <c>X-Forwarded-For</c> by itself.
    /// </remarks>
    public string? IpAddress
    {
        get
        {
            var address = _accessor.HttpContext?.Connection.RemoteIpAddress;
            if (address is null)
                return null;

            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }
    }
}
