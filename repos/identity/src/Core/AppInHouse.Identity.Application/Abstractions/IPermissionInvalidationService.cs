namespace AppInHouse.Identity.Application.Abstractions;

/// <summary>
/// To be called by the admin side after any change that affects permissions. Bumps the durable version (so every
/// node detects the change on its next request) and evicts the local cache entries.
/// </summary>
public interface IPermissionInvalidationService
{
    /// <summary>The user's roles, direct permissions, lock/active state or project membership changed.</summary>
    Task InvalidateUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>The project's state or its users' permissions changed.</summary>
    Task InvalidateProjectAsync(string projectCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// A role or a permission definition changed; these affect an unknown set of users, so everything is invalidated.
    /// </summary>
    Task InvalidateAllAsync(CancellationToken cancellationToken = default);
}
