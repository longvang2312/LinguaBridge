using AppInHouse.Identity.Application.Services;
using AppInHouse.Identity.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.Identity.UnitTests.Application;

public class PermissionInvalidationServiceTests
{
    private readonly FakeVersionStore _versions = new();
    private readonly FakeCache _cache = new();

    private PermissionInvalidationService CreateService()
        => new(_versions, _cache, NullLogger<PermissionInvalidationService>.Instance);

    [Fact]
    public async Task InvalidateUser_BumpsUserVersion_AndEvictsUserEntries()
    {
        await CreateService().InvalidateUserAsync("user-1");

        Assert.Equal(new[] { "User:user-1" }, _versions.Incremented);
        Assert.Equal(new[] { "user:user-1" }, _cache.Removed);
    }

    [Fact]
    public async Task InvalidateProject_BumpsNormalizedProjectVersion_AndEvictsProjectEntries()
    {
        await CreateService().InvalidateProjectAsync("erp");

        Assert.Equal(new[] { "Project:ERP" }, _versions.Incremented);
        Assert.Equal(new[] { "project:erp" }, _cache.Removed);
    }

    [Fact]
    public async Task InvalidateAll_BumpsGlobalVersion_AndClearsCache()
    {
        await CreateService().InvalidateAllAsync();

        Assert.Equal(new[] { "Global" }, _versions.Incremented);
        Assert.Equal(new[] { "all" }, _cache.Removed);
    }

    [Fact]
    public async Task CacheEvictionFailure_DoesNotFailTheOperation_BecauseTheVersionWasBumped()
    {
        _cache.Throw = new InvalidOperationException("cache down");

        await CreateService().InvalidateUserAsync("user-1");

        Assert.Single(_versions.Incremented);
    }

    [Fact]
    public async Task VersionStoreFailure_IsReportedToTheCaller()
    {
        var failing = new ThrowingVersionStore();
        var service = new PermissionInvalidationService(failing, _cache, NullLogger<PermissionInvalidationService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InvalidateUserAsync("user-1"));
        Assert.Empty(_cache.Removed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BlankArguments_AreRejected(string value)
    {
        var service = CreateService();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.InvalidateUserAsync(value));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.InvalidateProjectAsync(value));
    }

    private sealed class ThrowingVersionStore : AppInHouse.Identity.Application.Abstractions.IPermissionVersionStore
    {
        public Task<long> GetCombinedVersionAsync(IReadOnlyCollection<string> contextKeys, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException();

        public Task IncrementAsync(string contextKey, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("version store down");
    }
}
