using System.Security.Claims;
using AppInHouse.SharedServices.Api.Auth;

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
}
