using System.Net;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminApiHost;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminWorld;

namespace AppInHouse.Identity.IntegrationTests.AdminApi;

public class UsersApiTests
{
    private const string StrongPassword = "Str0ng!Passw0rd#1";

    private static object NewUser(string email = "new.user@test.local", string password = StrongPassword)
        => new { email, fullName = "New User", password };

    private static Task<bool> PasswordIsAsync(AdminApiHost api, string userId, string password)
        => api.ScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            return await users.CheckPasswordAsync((await users.FindByIdAsync(userId))!, password);
        });

    // ---- create / read ----

    [Fact]
    public async Task GlobalAdmin_CreatesAUser_WhoseCredentialsWork_AndTheAuditRecordHasNoPassword()
    {
        await using var api = await AdminApiHost.StartAsync();

        var response = await api.PostAsync("/api/v1/users", Root, NewUser());

        var created = await ExpectAsync(HttpStatusCode.Created, response);
        Assert.DoesNotContain(StrongPassword, created.ToString());
        var id = created.GetProperty("id").GetString()!;
        Assert.EndsWith($"/api/v1/users/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal("new.user@test.local", created.GetProperty("email").GetString());
        Assert.True(created.GetProperty("isActive").GetBoolean());
        Assert.True(await PasswordIsAsync(api, id, StrongPassword));

        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.EntityName == "User");
        Assert.Equal("CREATE", audit.Action);
        Assert.Equal(id, audit.EntityId);
        Assert.DoesNotContain(StrongPassword, audit.NewValuesJson);
        Assert.Contains("new.user@test.local", audit.NewValuesJson);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("alllowercase1!")]
    [InlineData("NoDigits!!!!!")]
    public async Task WeakPasswords_AreRejected_WithTheIdentityErrors(string password)
    {
        await using var api = await AdminApiHost.StartAsync();

        var problem = await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync("/api/v1/users", Root, NewUser(password: password)));

        Assert.True(problem.TryGetProperty("errors", out var errors) && errors.EnumerateObject().Any());
        Assert.DoesNotContain(password, problem.ToString());
        Assert.Empty(await api.AuditLogsAsync());
    }

    [Fact]
    public async Task DuplicateEmail_IsAConflict_AndInvalidEmailIsRejected()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Conflict, await api.PostAsync("/api/v1/users", Root, NewUser(email: $"{Outsider}@test.local")));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync("/api/v1/users", Root, NewUser(email: "not-an-email")));
    }

    [Fact]
    public async Task GlobalAdmin_ListsSearchesAndReadsUsers()
    {
        await using var api = await AdminApiHost.StartAsync();

        var all = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/users?pageSize=2", Root));
        Assert.Equal(5, all.GetProperty("totalCount").GetInt64());
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());

        var search = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/users?search=member-a", Root));
        var found = Assert.Single(search.GetProperty("items").EnumerateArray());
        Assert.Equal(MemberA, found.GetProperty("id").GetString());

        var one = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync($"/api/v1/users/{AdminA}", Root));
        Assert.Equal("User admin-a", one.GetProperty("fullName").GetString());
        Assert.False(one.TryGetProperty("passwordHash", out _));
        Assert.False(one.TryGetProperty("securityStamp", out _));

        await ExpectAsync(HttpStatusCode.NotFound, await api.GetAsync("/api/v1/users/nobody", Root));
    }

    [Theory]
    [InlineData(AdminA)]
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task OnlyGlobalAdministrators_CanManageUsers(string caller)
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync("/api/v1/users", caller));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync($"/api/v1/users/{MemberA}", caller));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync("/api/v1/users", caller, NewUser()));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PutAsync($"/api/v1/users/{MemberA}", caller, new { fullName = "X", isActive = false }));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync($"/api/v1/users/{MemberA}/lock", caller));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync($"/api/v1/users/{MemberA}/unlock", caller));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync($"/api/v1/users/{MemberA}/reset-password", caller, new { newPassword = StrongPassword }));

        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
        Assert.Empty(await api.AuditLogsAsync());
    }

    // ---- update ----

    [Fact]
    public async Task UpdatingAUser_ChangesTheName_AndAuditsOldAndNewValues()
    {
        await using var api = await AdminApiHost.StartAsync();

        var updated = await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/users/{MemberA}", Root, new { fullName = "Member Renamed", isActive = true }));

        Assert.Equal("Member Renamed", updated.GetProperty("fullName").GetString());
        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.EntityName == "User");
        Assert.Contains("User member-a", audit.OldValuesJson);
        Assert.Contains("Member Renamed", audit.NewValuesJson);
    }

    [Fact]
    public async Task DeactivatingAUser_RevokesTheirPermissionsAtOnce_AndReactivatingRestoresThem()
    {
        await using var api = await AdminApiHost.StartAsync();
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
        var stampBefore = await api.QueryIdentityAsync(identity => Task.FromResult(identity.Users.Single(u => u.Id == MemberA).SecurityStamp));

        await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/users/{MemberA}", Root, new { fullName = "Member", isActive = false }));

        Assert.False(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
        // Existing sessions are signed out by rotating the security stamp.
        Assert.NotEqual(stampBefore, await api.QueryIdentityAsync(identity => Task.FromResult(identity.Users.Single(u => u.Id == MemberA).SecurityStamp)));

        await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/users/{MemberA}", Root, new { fullName = "Member", isActive = true }));
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));
    }

    [Fact]
    public async Task AGlobalAdmin_CannotDeactivateThemselves_AndTheFlagIsRequired()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PutAsync($"/api/v1/users/{Root}", Root, new { fullName = "Root", isActive = false }));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PutAsync($"/api/v1/users/{MemberA}", Root, new { fullName = "Member" }));

        Assert.True(await api.QueryIdentityAsync(identity => Task.FromResult(identity.Users.Single(u => u.Id == Root).IsActive)));
        Assert.True(await api.QueryIdentityAsync(identity => Task.FromResult(identity.Users.Single(u => u.Id == MemberA).IsActive)));
    }

    // ---- lock / unlock ----

    [Fact]
    public async Task LockingAUser_RevokesTheirPermissionsAtOnce_AndUnlockingRestoresThem()
    {
        await using var api = await AdminApiHost.StartAsync();
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));

        var locked = await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/users/{MemberA}/lock", Root));

        Assert.True(locked.GetProperty("isLockedOut").GetBoolean());
        Assert.False(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));

        var unlocked = await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/users/{MemberA}/unlock", Root));
        Assert.False(unlocked.GetProperty("isLockedOut").GetBoolean());
        Assert.True(await api.HasPermissionAsync(MemberA, "ProjectA", "orders.create"));

        Assert.Equal(new[] { "LOCK", "UNLOCK" }, (await api.AuditLogsAsync()).Where(l => l.EntityName == "User").Select(l => l.Action));
    }

    [Fact]
    public async Task LockingUntilATime_SetsThatEnd_AndAPastTimeIsRejected()
    {
        await using var api = await AdminApiHost.StartAsync();
        var until = DateTimeOffset.UtcNow.AddHours(2);

        var locked = await ExpectAsync(HttpStatusCode.OK, await api.PostAsync($"/api/v1/users/{MemberA}/lock", Root, new { lockedUntil = until }));

        Assert.True(locked.GetProperty("isLockedOut").GetBoolean());
        Assert.True(Math.Abs((locked.GetProperty("lockoutEnd").GetDateTimeOffset() - until).TotalSeconds) < 1);
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync($"/api/v1/users/{AdminB}/lock", Root, new { lockedUntil = DateTimeOffset.UtcNow.AddMinutes(-5) }));
    }

    [Fact]
    public async Task AGlobalAdmin_CannotLockThemselves_AndUnknownUsersAreNotFound()
    {
        await using var api = await AdminApiHost.StartAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync($"/api/v1/users/{Root}/lock", Root));
        await ExpectAsync(HttpStatusCode.NotFound, await api.PostAsync("/api/v1/users/nobody/lock", Root));
        await ExpectAsync(HttpStatusCode.NotFound, await api.PostAsync("/api/v1/users/nobody/unlock", Root));
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/projects", Root)).StatusCode); // still in charge
    }

    // ---- reset password ----

    [Fact]
    public async Task ResettingAPassword_ReplacesTheOldOne_AndNeitherAppearsInTheAudit()
    {
        await using var api = await AdminApiHost.StartAsync();
        await ExpectAsync(HttpStatusCode.NoContent, await api.PostAsync($"/api/v1/users/{Outsider}/reset-password", Root, new { newPassword = StrongPassword }));
        const string newer = "An0ther!Str0ng#Pass";

        var response = await api.PostAsync($"/api/v1/users/{Outsider}/reset-password", Root, new { newPassword = newer });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await PasswordIsAsync(api, Outsider, newer));
        Assert.False(await PasswordIsAsync(api, Outsider, StrongPassword));
        var audits = (await api.AuditLogsAsync()).Where(l => l.Action == "RESET_PASSWORD").ToList();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.Equal(Root, a.UserId);
            Assert.DoesNotContain(newer, a.NewValuesJson);
            Assert.DoesNotContain(StrongPassword, a.NewValuesJson);
        });
    }

    [Fact]
    public async Task ResettingToAWeakPassword_IsRejected_AndKeepsTheOldPassword()
    {
        await using var api = await AdminApiHost.StartAsync();
        await ExpectAsync(HttpStatusCode.NoContent, await api.PostAsync($"/api/v1/users/{Outsider}/reset-password", Root, new { newPassword = StrongPassword }));

        var problem = await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync($"/api/v1/users/{Outsider}/reset-password", Root, new { newPassword = "weak" }));

        Assert.DoesNotContain("weak\"", problem.ToString());
        Assert.True(await PasswordIsAsync(api, Outsider, StrongPassword));
        await ExpectAsync(HttpStatusCode.NotFound, await api.PostAsync("/api/v1/users/nobody/reset-password", Root, new { newPassword = StrongPassword }));
    }
}
