namespace AppInHouse.Identity.Application.Authorization;

/// <summary>
/// Cache entry keys and <c>PermissionCacheVersions.ContextKey</c> values. Project codes are upper-cased so that
/// <c>erp</c> and <c>ERP</c> share one cache entry and one version counter.
/// </summary>
public static class PermissionCacheKeys
{
    /// <summary>Context key bumped when permission definitions change for everyone (e.g. a role is edited).</summary>
    public const string GlobalContext = "Global";

    public static string Entry(string userId, string projectCode) => $"Perms:{userId}:{NormalizeProject(projectCode)}";

    public static string UserContext(string userId) => $"User:{userId}";

    public static string ProjectContext(string projectCode) => $"Project:{NormalizeProject(projectCode)}";

    /// <summary>Every context key whose version influences the permissions of (user, project).</summary>
    public static IReadOnlyCollection<string> VersionContexts(string userId, string projectCode)
        => new[] { GlobalContext, UserContext(userId), ProjectContext(projectCode) };

    public static string NormalizeProject(string projectCode) => projectCode.Trim().ToUpperInvariant();
}
