namespace AppInHouse.Identity.Application.Abstractions;

public interface IPermissionService
{
    /// <summary>
    /// Effective permissions of the user in the project (roles + direct grants - direct revocations), served from the
    /// version-validated cache. Exceptions from the cache or data source propagate to the caller.
    /// </summary>
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(string userId, string projectCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the user currently holds <paramref name="permission"/> in the project. Deny-by-default: blank
    /// arguments and any failure of the cache or data source yield <c>false</c>.
    /// </summary>
    Task<bool> HasPermissionAsync(string userId, string projectCode, string permission, CancellationToken cancellationToken = default);
}
