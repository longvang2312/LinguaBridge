using System.Security.Claims;
using AppInHouse.Identity.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace AppInHouse.Identity.ClientSdk;

/// <summary>
/// Checks, at request time, that the authenticated user holds the required permission in the project the request
/// targets. The permission lookup (and its version-validated cache) lives in <see cref="IPermissionService"/>, so
/// a revoked permission stops working immediately even though the user's token is still valid.
/// </summary>
/// <remarks>
/// Every non-success path calls <c>context.Fail()</c>, so no other handler can grant access: missing user, missing or
/// ambiguous project, missing permission, or any exception (deny-by-default).
/// </remarks>
public class ProjectAwarePermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionService _permissionService;
    private readonly IProjectContextResolver _projectResolver;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ProjectAwarePermissionHandler> _logger;

    public ProjectAwarePermissionHandler(
        IPermissionService permissionService,
        IProjectContextResolver projectResolver,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ProjectAwarePermissionHandler> logger)
    {
        _permissionService = permissionService;
        _projectResolver = projectResolver;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        try
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(userId))
            {
                Deny(context, "The token has no subject.");
                return;
            }

            var httpContext = GetHttpContext(context);
            var projectCode = httpContext is null ? null : ResolveProject(httpContext);
            if (httpContext is null || string.IsNullOrWhiteSpace(projectCode))
            {
                Deny(context, "No valid project was specified for the request.");
                return;
            }

            var allowed = await _permissionService.HasPermissionAsync(
                userId, projectCode, requirement.Permission, httpContext.RequestAborted);

            if (allowed)
            {
                context.Succeed(requirement);
                return;
            }

            _logger.LogInformation(
                "Denied {Permission} to user {UserId} in project {ProjectCode}.", requirement.Permission, userId, projectCode);
            Deny(context, $"Missing permission '{requirement.Permission}'.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authorization of {Permission} failed unexpectedly; denying access.", requirement.Permission);
            Deny(context, "Authorization could not be evaluated.");
        }
    }

    private string? ResolveProject(HttpContext httpContext)
        => httpContext.Features.Get<ProjectContext>() is { } feature
            ? feature.ProjectCode
            : _projectResolver.Resolve(httpContext);

    private HttpContext? GetHttpContext(AuthorizationHandlerContext context) => context.Resource switch
    {
        HttpContext httpContext => httpContext,
        AuthorizationFilterContext filterContext => filterContext.HttpContext,
        _ => _httpContextAccessor.HttpContext,
    };

    private void Deny(AuthorizationHandlerContext context, string reason)
        => context.Fail(new AuthorizationFailureReason(this, reason));
}
