using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AppInHouse.Identity.ClientSdk;

/// <summary>
/// Reads the project code from the route value and/or the header. When both are present they must agree: a request
/// that names two different projects is ambiguous and gets no project (and is therefore denied).
/// </summary>
public partial class ProjectContextResolver : IProjectContextResolver
{
    private const int MaxLength = 50;

    private readonly AppInHouseAuthorizationOptions _options;

    public ProjectContextResolver(IOptions<AppInHouseAuthorizationOptions> options) => _options = options.Value;

    public string? Resolve(HttpContext httpContext)
    {
        string? header = null;
        if (httpContext.Request.Headers.TryGetValue(_options.ProjectCodeHeaderName, out var headerValues))
        {
            // Repeated headers are ambiguous.
            if (headerValues.Count != 1 || !TryNormalize(headerValues[0], out header))
                return null;
        }

        string? route = null;
        var routeValue = httpContext.GetRouteValue(_options.ProjectCodeRouteKey)?.ToString();
        if (routeValue is not null && !TryNormalize(routeValue, out route))
            return null;

        if (header is not null && route is not null && !string.Equals(header, route, StringComparison.OrdinalIgnoreCase))
            return null;

        return route ?? header;
    }

    private static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > MaxLength || !AllowedCharacters().IsMatch(normalized))
        {
            normalized = null;
            return false;
        }

        return true;
    }

    // Letters, digits, space, underscore, dot and hyphen. Notably excludes ':' used in cache keys and control characters.
    [GeneratedRegex(@"^[\p{L}\p{N} _.\-]+$")]
    private static partial Regex AllowedCharacters();
}
