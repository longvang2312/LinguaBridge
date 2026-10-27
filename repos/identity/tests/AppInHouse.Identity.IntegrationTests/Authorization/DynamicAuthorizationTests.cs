using System.Diagnostics;
using System.Net;
using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Authorization;
using AppInHouse.Identity.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.Identity.IntegrationTests.Authorization;

/// <summary>
/// Acceptance scenarios of TASK-004: dynamic permissions, project isolation and instant revocation, exercised
/// through real HTTP requests against an API protected by <c>[HasPermission]</c>.
/// </summary>
public class DynamicAuthorizationTests
{
    private const string UserId = "user-1";

    private static async Task<(AuthorizationTestHost Host, Scenario Scenario)> StartAsync(
        Action<IServiceCollection>? overrideServices = null, bool seedUser = true)
    {
        var host = await AuthorizationTestHost.StartAsync(overrideServices);
        var scenario = new Scenario();
        await host.SeedAsync((identity, auth) =>
        {
            scenario.Seed(identity, auth);
            if (seedUser)
                scenario.GiveOrderManagerInProjectA(identity, auth, UserId);
        });
        return (host, scenario);
    }

    // ---- 1. Allowed ----

    [Fact]
    public async Task UserWithRoleInProjectA_CanCallProjectAEndpoint()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        var response = await host.CreateOrderAsync(UserId, "ProjectA");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProjectCanAlsoBeTakenFromTheRoute()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        var response = await host.SendAsync(HttpMethod.Post, "/api/ProjectA/orders", UserId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- 2. Project isolation / IDOR ----

    [Fact]
    public async Task SameToken_CalledForProjectB_IsForbidden()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectB")).StatusCode);
    }

    [Fact]
    public async Task SameToken_ViaRouteForProjectB_IsForbidden()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        var response = await host.SendAsync(HttpMethod.Post, "/api/ProjectB/orders", UserId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HeaderForOwnProject_CannotOverrideRouteOfAnotherProject()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        // Header says A (allowed) but the route addresses B: ambiguous, so denied.
        var response = await host.SendAsync(HttpMethod.Post, "/api/ProjectB/orders", UserId, "ProjectA");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("DoesNotExist")]
    [InlineData("ProjectA:admin")]
    [InlineData("")]
    public async Task MissingUnknownOrMalformedProject_IsForbidden(string? projectCode)
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        var response = await host.CreateOrderAsync(UserId, projectCode);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        var response = await host.CreateOrderAsync(userId: null, projectCode: "ProjectA");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedSubjectThatIsNotAUser_IsForbidden()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        // e.g. a client-credentials token whose subject is a client id
        var response = await host.CreateOrderAsync("internal-service", "ProjectA");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RoleHeldInAnotherProject_DoesNotLeakThePermissionsOfThisProject()
    {
        var (host, scenario) = await StartAsync();
        await using var _ = host;
        // The user holds OrderManager in B as well, but the role's permission belongs to project A.
        await host.SeedAsync((_, auth) =>
        {
            auth.ProjectUserAssignments.Add(new ProjectUserAssignment(scenario.ProjectB.Id, UserId));
            auth.UserRoleProjectAssignments.Add(new UserRoleProjectAssignment(UserId, Scenario.OrderManagerRoleId, scenario.ProjectB.Id));
        });

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectB")).StatusCode);
    }

    // ---- 3. Instant revocation with the same, still valid token ----

    [Fact]
    public async Task RemovingPermissionFromRole_IsEffectiveImmediatelyAfterInvalidation()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode); // warms the cache
        await host.SeedAsync((_, auth) => auth.RolePermissions.RemoveRange(auth.RolePermissions.ToList()));

        // Without invalidation the cached decision is still served...
        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);

        // ...and the admin's invalidation flips it at once.
        var clock = Stopwatch.StartNew();
        await host.InvalidateUserAsync(UserId);
        var response = await host.CreateOrderAsync(UserId, "ProjectA");
        clock.Stop();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Revocation took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RemovingTheRoleAssignment_IsEffectiveImmediatelyAfterInvalidation()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
        await host.SeedAsync((_, auth) => auth.UserRoleProjectAssignments.RemoveRange(auth.UserRoleProjectAssignments.ToList()));
        await host.InvalidateUserAsync(UserId);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("project")]
    [InlineData("all")]
    public async Task EveryInvalidationScope_RevokesTheCachedDecision(string scope)
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
        await host.SeedAsync((_, auth) => auth.RolePermissions.RemoveRange(auth.RolePermissions.ToList()));

        await host.ScopeAsync(sp =>
        {
            var invalidation = sp.GetRequiredService<IPermissionInvalidationService>();
            return scope switch
            {
                "user" => invalidation.InvalidateUserAsync(UserId),
                "project" => invalidation.InvalidateProjectAsync("ProjectA"),
                _ => invalidation.InvalidateAllAsync(),
            };
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task InvalidatingAnotherUser_DoesNotAffectThisOne()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        await host.InvalidateUserAsync("someone-else");

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task VersionBumpAlone_InvalidatesTheCache_AsOnAnotherNode()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
        await host.SeedAsync((_, auth) => auth.RolePermissions.RemoveRange(auth.RolePermissions.ToList()));

        // Another node performed the admin change: it bumped the shared version, but THIS node's cache was not
        // evicted. The per-request version check still catches it.
        await host.ScopeAsync(sp => sp.GetRequiredService<IPermissionVersionStore>().IncrementAsync(PermissionCacheKeys.UserContext(UserId)));

        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task GrantingThePermissionBack_IsEffectiveAfterInvalidation()
    {
        var (host, scenario) = await StartAsync();
        await using var _ = host;
        await host.SeedAsync((_, auth) => auth.RolePermissions.RemoveRange(auth.RolePermissions.ToList()));
        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);

        await host.SeedAsync((_, auth) => auth.RolePermissions.Add(new RolePermission(Scenario.OrderManagerRoleId, scenario.OrdersCreateA.Id)));
        await host.InvalidateUserAsync(UserId);

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task DeactivatingTheUser_BlocksTheSameToken_AfterInvalidation()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;
        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);

        await host.SeedAsync((identity, _) => identity.Users.Single(u => u.Id == UserId).IsActive = false);
        await host.InvalidateUserAsync(UserId);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task LockingOutTheUser_BlocksTheSameToken_AfterInvalidation()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;
        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);

        await host.SeedAsync((identity, _) => identity.Users.Single(u => u.Id == UserId).LockoutEnd = DateTimeOffset.UtcNow.AddHours(1));
        await host.InvalidateUserAsync(UserId);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task BlockingTheUserInTheProject_BlocksTheSameToken_AfterInvalidation()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;
        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);

        await host.SeedAsync((_, auth) => auth.ProjectUserAssignments.Single(a => a.UserId == UserId).Block());
        await host.InvalidateUserAsync(UserId);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task DeactivatingTheProject_BlocksEveryone_AfterProjectInvalidation()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;
        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);

        await host.SeedAsync((_, auth) => auth.Projects.Single(p => p.ProjectCode == "ProjectA").Deactivate());
        await host.ScopeAsync(sp => sp.GetRequiredService<IPermissionInvalidationService>().InvalidateProjectAsync("ProjectA"));

        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    // ---- 4. Direct revocation / direct grants ----

    [Fact]
    public async Task DirectRevocation_BeatsThePermissionGrantedByTheRole()
    {
        var (host, scenario) = await StartAsync();
        await using var _ = host;
        await host.SeedAsync((_, auth) =>
            auth.UserPermissionAssignments.Add(new UserPermissionAssignment(UserId, scenario.OrdersCreateA.Id, scenario.ProjectA.Id, isRevoked: true)));

        // The role still contains orders.create, yet the user is denied.
        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task DirectRevocation_AddedLater_TakesEffectAfterInvalidation_AndCanBeLifted()
    {
        var (host, scenario) = await StartAsync();
        await using var _ = host;
        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);

        await host.SeedAsync((_, auth) =>
            auth.UserPermissionAssignments.Add(new UserPermissionAssignment(UserId, scenario.OrdersCreateA.Id, scenario.ProjectA.Id, isRevoked: true)));
        await host.InvalidateUserAsync(UserId);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);

        await host.SeedAsync((_, auth) => auth.UserPermissionAssignments.RemoveRange(auth.UserPermissionAssignments.ToList()));
        await host.InvalidateUserAsync(UserId);
        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task DirectRevocation_OnlyAffectsTheProjectItWasMadeIn()
    {
        var (host, scenario) = await StartAsync();
        await using var _ = host;
        await host.SeedAsync((_, auth) =>
            auth.UserPermissionAssignments.Add(new UserPermissionAssignment(UserId, scenario.OrdersCreateA.Id, scenario.ProjectB.Id, isRevoked: true)));

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync(UserId, "ProjectA")).StatusCode);
    }

    [Fact]
    public async Task DirectGrant_AllowsAUserWithoutAnyRole()
    {
        var (host, scenario) = await StartAsync(seedUser: false);
        await using var _ = host;
        await host.SeedAsync((identity, auth) =>
        {
            identity.Users.Add(Scenario.NewUser("direct-user"));
            auth.UserPermissionAssignments.Add(new UserPermissionAssignment("direct-user", scenario.OrdersCreateA.Id, scenario.ProjectA.Id));
        });

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync("direct-user", "ProjectA")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync("direct-user", "ProjectB")).StatusCode);
    }

    // ---- Admins ----

    [Fact]
    public async Task ProjectAdmin_HasEverythingInTheirProject_ButNothingElsewhere()
    {
        var (host, scenario) = await StartAsync(seedUser: false);
        await using var _ = host;
        await host.SeedAsync((identity, auth) =>
        {
            identity.Users.Add(Scenario.NewUser("proj-admin"));
            auth.ProjectUserAssignments.Add(new ProjectUserAssignment(scenario.ProjectA.Id, "proj-admin", isProjectAdmin: true));
        });

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync("proj-admin", "ProjectA")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/orders", "proj-admin", "ProjectA")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.CreateOrderAsync("proj-admin", "ProjectB")).StatusCode);
    }

    [Fact]
    public async Task GlobalAdmin_HasEverythingInEveryActiveProject()
    {
        var (host, _) = await StartAsync(seedUser: false);
        await using var _ = host;
        await host.SeedAsync((identity, _) =>
        {
            identity.Users.Add(Scenario.NewUser("root"));
            identity.UserRoles.Add(new IdentityUserRole<string> { UserId = "root", RoleId = "role-global-admin" });
        });

        Assert.Equal(HttpStatusCode.OK, (await host.CreateOrderAsync("root", "ProjectA")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/orders", "root", "ProjectB")).StatusCode);
    }

    [Fact]
    public async Task GlobalAdmin_BlockedInAProject_IsDeniedThere()
    {
        var (host, scenario) = await StartAsync(seedUser: false);
        await using var _ = host;
        await host.SeedAsync((identity, auth) =>
        {
            identity.Users.Add(Scenario.NewUser("root"));
            identity.UserRoles.Add(new IdentityUserRole<string> { UserId = "root", RoleId = "role-global-admin" });
            var blocked = new ProjectUserAssignment(scenario.ProjectA.Id, "root");
            blocked.Block();
            auth.ProjectUserAssignments.Add(blocked);
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Get, "/api/orders", "root", "ProjectA")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/orders", "root", "ProjectB")).StatusCode);
    }

    // ---- Several [HasPermission] attributes ----

    [Fact]
    public async Task MultipleAttributes_AllMustBeSatisfied()
    {
        var (host, scenario) = await StartAsync(seedUser: false);
        await using var _ = host;
        var delete = new Permission("orders.delete", "Delete orders", "Orders");
        await host.SeedAsync((identity, auth) =>
        {
            auth.Permissions.Add(delete);
            identity.Users.AddRange(Scenario.NewUser("viewer"), Scenario.NewUser("manager"));
            foreach (var user in new[] { "viewer", "manager" })
            {
                auth.ProjectUserAssignments.Add(new ProjectUserAssignment(scenario.ProjectA.Id, user));
                auth.UserPermissionAssignments.Add(new UserPermissionAssignment(user, scenario.OrdersView.Id, scenario.ProjectA.Id));
            }

            auth.UserPermissionAssignments.Add(new UserPermissionAssignment("manager", delete.Id, scenario.ProjectA.Id));
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Delete, "/api/orders", "viewer", "ProjectA")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Delete, "/api/orders", "manager", "ProjectA")).StatusCode);
    }

    // ---- Fail-safe: deny by default ----

    [Fact]
    public async Task WhenTheDataSourceFails_AccessIsDenied_NotGranted_AndNoServerError()
    {
        var (host, _) = await StartAsync(services => services.AddScoped<IPermissionDataSource, FailingDataSource>());
        await using var _ = host;

        var response = await host.CreateOrderAsync(UserId, "ProjectA");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheCacheFails_AccessIsDenied()
    {
        var (host, _) = await StartAsync(services => services.AddSingleton<IPermissionCacheService, FailingCache>());
        await using var _ = host;

        var response = await host.CreateOrderAsync(UserId, "ProjectA");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed class FailingDataSource : IPermissionDataSource
    {
        private static Exception Failure => new InvalidOperationException("database unavailable");

        public Task<UserAccessState?> GetUserAccessStateAsync(string userId, CancellationToken cancellationToken = default) => throw Failure;
        public Task<bool> IsGlobalAdminAsync(string userId, CancellationToken cancellationToken = default) => throw Failure;
        public Task<ProjectAccessInfo?> FindProjectAsync(string projectCode, CancellationToken cancellationToken = default) => throw Failure;
        public Task<ProjectMembership?> GetProjectMembershipAsync(Guid projectId, string userId, CancellationToken cancellationToken = default) => throw Failure;
        public Task<IReadOnlyCollection<string>> GetAllPermissionCodesAsync(Guid projectId, CancellationToken cancellationToken = default) => throw Failure;
        public Task<IReadOnlyCollection<string>> GetRolePermissionCodesAsync(string userId, Guid projectId, CancellationToken cancellationToken = default) => throw Failure;
        public Task<IReadOnlyCollection<DirectPermissionAssignment>> GetDirectPermissionsAsync(string userId, Guid projectId, CancellationToken cancellationToken = default) => throw Failure;
    }

    private sealed class FailingCache : IPermissionCacheService
    {
        private static Exception Failure => new InvalidOperationException("cache unavailable");

        public Task<CachedPermissionsModel?> GetAsync(string userId, string projectCode, CancellationToken cancellationToken = default) => throw Failure;
        public Task SetAsync(string userId, string projectCode, CachedPermissionsModel model, CancellationToken cancellationToken = default) => throw Failure;
        public Task RemoveUserAsync(string userId, CancellationToken cancellationToken = default) => throw Failure;
        public Task RemoveProjectAsync(string projectCode, CancellationToken cancellationToken = default) => throw Failure;
        public Task ClearAsync(CancellationToken cancellationToken = default) => throw Failure;
    }
}
