using Microsoft.AspNetCore.Authorization;

namespace AppInHouse.Identity.ClientSdk;

/// <summary>
/// Requires the caller to hold <paramref name="permission"/> in the project of the current request.
/// Multiple attributes must all be satisfied.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class HasPermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "PERM:";

    public HasPermissionAttribute(string permission) : base(policy: PolicyPrefix + Validate(permission))
    {
        Permission = permission.Trim();
    }

    public string Permission { get; }

    private static string Validate(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return permission.Trim();
    }
}
