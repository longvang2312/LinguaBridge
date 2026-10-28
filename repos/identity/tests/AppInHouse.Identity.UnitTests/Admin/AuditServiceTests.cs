using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Infrastructure.Persistence.Admin;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.Identity.UnitTests.Admin;

public class AuditServiceTests
{
    private sealed class FixedCaller(string? userId, string? ip) : ICurrentCaller
    {
        public string? UserId { get; } = userId;

        public string? IpAddress { get; } = ip;
    }

    private readonly DbContextOptions<AppAuditDbContext> _options =
        new DbContextOptionsBuilder<AppAuditDbContext>().UseInMemoryDatabase("audit-" + Guid.NewGuid()).Options;

    private AuditService CreateService(string? userId = "admin-1", string? ip = "10.1.2.3")
        => new(new AppAuditDbContext(_options), new FixedCaller(userId, ip), NullLogger<AuditService>.Instance);

    private async Task<AppInHouse.Identity.Domain.Entities.AuditLog> SingleAsync()
    {
        await using var context = new AppAuditDbContext(_options);
        return await context.AuditLogs.SingleAsync();
    }

    [Fact]
    public async Task Write_RecordsTheActor_TheIp_TheEntity_AndBothSnapshots()
    {
        var projectId = Guid.NewGuid();

        await CreateService().WriteAsync(new AuditEntry(
            AuditActions.Update, AuditEntities.Project, "p-1", projectId,
            OldValues: new { Name = "Old" }, NewValues: new { Name = "New", Nested = new { Value = 1 } }));

        var log = await SingleAsync();
        Assert.Equal("UPDATE", log.Action);
        Assert.Equal("Project", log.EntityName);
        Assert.Equal("p-1", log.EntityId);
        Assert.Equal("admin-1", log.UserId);
        Assert.Equal("10.1.2.3", log.IpAddress);
        Assert.Equal(projectId, log.ProjectId);
        Assert.Equal("{\"name\":\"Old\"}", log.OldValuesJson);
        Assert.Equal("{\"name\":\"New\",\"nested\":{\"value\":1}}", log.NewValuesJson);
        Assert.Equal(DateTimeKind.Utc, log.Timestamp.Kind);
    }

    [Fact]
    public async Task Write_WithoutSnapshots_StoresNulls()
    {
        await CreateService(userId: null, ip: null).WriteAsync(new AuditEntry("CREATE", "User", "u1"));

        var log = await SingleAsync();
        Assert.Null(log.OldValuesJson);
        Assert.Null(log.NewValuesJson);
        Assert.Null(log.UserId);
        Assert.Null(log.IpAddress);
        Assert.Null(log.ProjectId);
    }

    [Fact]
    public async Task PasswordAndSecretFields_AreRedacted_AtAnyDepth_WhateverTheirCasing()
    {
        await CreateService().WriteAsync(new AuditEntry(
            "CREATE", "User", "u1",
            NewValues: new
            {
                Email = "a@b.c",
                Password = "p@ss",
                NewPassword = "n3w",
                ClientSecret = "s3cret",
                passwordHash = "hash",
                Nested = new { Secret = "x", Keep = "visible" },
                List = new[] { new { OldPassword = "o", Name = "item" } },
            }));

        var json = (await SingleAsync()).NewValuesJson!;

        foreach (var leaked in new[] { "p@ss", "n3w", "s3cret", "hash", "\"x\"", "\"o\"" })
            Assert.DoesNotContain(leaked, json);
        Assert.Contains("[REDACTED]", json);
        Assert.Contains("a@b.c", json);
        Assert.Contains("visible", json);
        Assert.Contains("item", json);
    }

    [Fact]
    public async Task OverlongEntityFields_AreRejected_NotSilentlyTruncated()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            CreateService().WriteAsync(new AuditEntry(new string('A', 51), "User", "u1")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            CreateService().WriteAsync(new AuditEntry("CREATE", "User", new string('1', 101))));
    }
}
