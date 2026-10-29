using LinguaBridge.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;

namespace LinguaBridge.Api.Auth;

public static class Policies
{
    /// <summary>Content administration. Granted by role alone: Vip is a learner perk owned by Billing, never an admin right.</summary>
    public const string Admin = "Admin";
}

public static class Roles
{
    /// <summary>The role the STS puts in the access tokens of platform administrators.</summary>
    public const string GlobalAdmin = "GlobalAdmin";
}

internal sealed class AdminRequirement : IAuthorizationRequirement;

internal sealed class AdminAuthorizationHandler(ICurrentUser currentUser) : AuthorizationHandler<AdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (currentUser.IsAuthenticated && currentUser.IsInRole(Roles.GlobalAdmin))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
