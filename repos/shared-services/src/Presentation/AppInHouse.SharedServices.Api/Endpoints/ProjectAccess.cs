using System.Security.Claims;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Application.Configurations;

namespace AppInHouse.SharedServices.Api.Endpoints;

/// <summary>Which project a caller may send for or read the logs of (shared by the email and chat endpoints).</summary>
public static class ProjectAccess
{
    /// <summary>The project whose keys may act for every project (the platform's own).</summary>
    public const string PlatformProject = "default";

    /// <summary>
    /// A key acts for its own project; the platform project's keys (and bearer tokens, which carry no project) may name any project.
    /// With <paramref name="allowAll"/> a platform caller that names none gets every project (null).
    /// </summary>
    public static bool TryResolve(ClaimsPrincipal user, string? requested, out string? project, bool allowAll = false)
    {
        var own = user.FindFirst(ApiKeyDefaults.ProjectClaim)?.Value;
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim().ToLowerInvariant();

        if (own is null || own == PlatformProject)
        {
            project = wanted ?? (allowAll ? null : PlatformProject);
            return true;
        }

        project = wanted ?? own;
        return project == own;
    }

    /// <summary>A caller of the platform: a key of the "default" project, or a bearer token (which carries no project).</summary>
    public static bool IsPlatform(ClaimsPrincipal user)
    {
        var own = user.FindFirst(ApiKeyDefaults.ProjectClaim)?.Value;
        return own is null || own == PlatformProject;
    }

    /// <summary>Whether the caller may act on a resource of <paramref name="project"/>: its own project, or any project for the platform.</summary>
    public static bool CanAct(ClaimsPrincipal user, string project) => TryResolve(user, project, out _);

    /// <summary>The 403 answer (RFC 7807) for a resource of another project.</summary>
    public static IResult Forbidden(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not your project", detail: detail);

    /// <summary>
    /// For "test a saved configuration": null when the caller may use it (or when no saved configuration is named, because then nothing
    /// saved is touched), otherwise the 403. A configuration that does not exist is a 404 (thrown, like everywhere else).
    /// </summary>
    public static async Task<IResult?> CheckConfigurationAsync(ClaimsPrincipal user, Guid? configurationId, IChannelConfigManager manager, CancellationToken cancellationToken)
    {
        if (configurationId is not { } id) return null;
        var configuration = await manager.GetAsync(id, cancellationToken);
        return CanAct(user, configuration.ProjectCode) ? null : Forbidden("This configuration belongs to another project.");
    }
}
