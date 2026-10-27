using Microsoft.AspNetCore.Http;

namespace AppInHouse.Identity.ClientSdk;

/// <summary>The project the current request targets, available as an HttpContext feature.</summary>
public sealed record ProjectContext(string? ProjectCode);

/// <summary>
/// Resolves the project once per request and publishes it as a <see cref="ProjectContext"/> feature.
/// Place it after <c>UseRouting()</c> (so route values exist) and before <c>UseAuthorization()</c>.
/// It never rejects a request itself; authorization denies requests without a valid project.
/// </summary>
public class ProjectContextMiddleware
{
    private readonly RequestDelegate _next;

    public ProjectContextMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext httpContext, IProjectContextResolver resolver)
    {
        httpContext.Features.Set(new ProjectContext(resolver.Resolve(httpContext)));
        return _next(httpContext);
    }
}
