using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Security;

namespace AppInHouse.Identity.UnitTests.Admin;

public class AdminSecurityTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();

    private static AdminCaller Caller(bool eligible = true, bool global = false, params Guid[] projects)
        => new("caller", new AdminAccess(eligible, global, projects.ToHashSet()));

    // ---- AdminCaller ----

    [Fact]
    public void GlobalAdmin_MayDoEverything()
    {
        var caller = Caller(global: true);

        Assert.True(caller.IsAnyAdmin);
        Assert.True(caller.CanAdminister(ProjectA));
        caller.RequireGlobalAdmin();
        caller.RequireAnyAdmin();
        caller.RequireProjectAdmin(ProjectB);
    }

    [Fact]
    public void ProjectAdmin_MayOnlyAdministerTheirOwnProjects()
    {
        var caller = Caller(projects: ProjectA);

        Assert.True(caller.IsAnyAdmin);
        Assert.True(caller.CanAdminister(ProjectA));
        Assert.False(caller.CanAdminister(ProjectB));
        caller.RequireAnyAdmin();
        caller.RequireProjectAdmin(ProjectA);
        Assert.Throws<ForbiddenException>(() => caller.RequireProjectAdmin(ProjectB));
        Assert.Throws<ForbiddenException>(caller.RequireGlobalAdmin);
    }

    [Fact]
    public void UserWithoutAdminRights_MayDoNothing()
    {
        var caller = Caller();

        Assert.False(caller.IsAnyAdmin);
        Assert.Throws<ForbiddenException>(caller.RequireAnyAdmin);
        Assert.Throws<ForbiddenException>(caller.RequireGlobalAdmin);
        Assert.Throws<ForbiddenException>(() => caller.RequireProjectAdmin(ProjectA));
    }

    [Fact]
    public void AnIneligibleAccount_LosesEveryRight_EvenIfTheDataSaysGlobalAdmin()
    {
        var caller = Caller(eligible: false, global: true, ProjectA);

        Assert.False(caller.IsGlobalAdmin);
        Assert.False(caller.IsAnyAdmin);
        Assert.False(caller.CanAdminister(ProjectA));
        Assert.Throws<ForbiddenException>(caller.RequireAnyAdmin);
    }

    // ---- AdminAuthorizer ----

    private sealed class FixedCaller(string? userId) : ICurrentCaller
    {
        public string? UserId { get; } = userId;

        public string? IpAddress => null;
    }

    private sealed class CountingReader(AdminAccess access) : IAdminAccessReader
    {
        public int Reads { get; private set; }

        public Task<AdminAccess> ReadAsync(string userId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(access);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AnAnonymousCaller_IsForbidden_WithoutReadingAnything(string? userId)
    {
        var reader = new CountingReader(AdminAccess.None);

        await Assert.ThrowsAsync<ForbiddenException>(() => new AdminAuthorizer(new FixedCaller(userId), reader).GetCallerAsync());

        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task TheRightsAreReadOncePerRequest()
    {
        var reader = new CountingReader(new AdminAccess(true, true, new HashSet<Guid>()));
        var authorizer = new AdminAuthorizer(new FixedCaller("root"), reader);

        var first = await authorizer.GetCallerAsync();
        var second = await authorizer.GetCallerAsync();

        Assert.Same(first, second);
        Assert.Equal(1, reader.Reads);
        Assert.Equal("root", first.UserId);
        Assert.True(first.IsGlobalAdmin);
    }

    [Fact]
    public void NoAccess_HasNoRights()
    {
        Assert.False(AdminAccess.None.IsEligible);
        Assert.False(AdminAccess.None.IsGlobalAdmin);
        Assert.Empty(AdminAccess.None.AdministeredProjectIds);
    }

    // ---- paging ----

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 20)]
    [InlineData(500, 100)]
    public void ValidPaging_IsAccepted(int page, int pageSize)
        => new PagingQuery { Page = page, PageSize = pageSize }.EnsureValid();

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(1, -5)]
    public void InvalidPaging_IsRejected(int page, int pageSize)
        => Assert.Throws<BadRequestException>(() => new PagingQuery { Page = page, PageSize = pageSize }.EnsureValid());

    [Fact]
    public void PagingDefaults_AreSensible()
    {
        var query = new PagingQuery();

        Assert.Equal(1, query.Page);
        Assert.Equal(PagingQuery.DefaultPageSize, query.PageSize);
        Assert.True(PagingQuery.DefaultPageSize <= PagingQuery.MaxPageSize);
    }
}
