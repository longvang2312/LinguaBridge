using Microsoft.AspNetCore.Http;

namespace AppInHouse.Identity.ClientSdk;

public interface IProjectContextResolver
{
    /// <summary>
    /// The project code the request targets, or null when it is missing, malformed or ambiguous
    /// (callers must then deny access).
    /// </summary>
    string? Resolve(HttpContext httpContext);
}
