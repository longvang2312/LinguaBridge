using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace AppInHouse.Identity.ClientSdk;

/// <summary>
/// Builds a policy on demand for every <c>PERM:{permission}</c> name, so permissions need no up-front registration.
/// Any other policy name is delegated to the default provider.
/// </summary>
public class DynamicPermissionPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public DynamicPermissionPolicyProvider(IOptions<AuthorizationOptions> options) : base(options)
    {
    }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var permission = policyName[HasPermissionAttribute.PolicyPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(permission))
            {
                return new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission))
                    .Build();
            }
        }

        return await base.GetPolicyAsync(policyName);
    }
}
