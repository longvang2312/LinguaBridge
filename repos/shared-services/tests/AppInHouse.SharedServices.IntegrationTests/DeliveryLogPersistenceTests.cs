using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using AppInHouse.SharedServices.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.IntegrationTests;

public class SqliteDeliveryLogTests : IAsyncLifetime
{
    private PersistenceHost _host = null!;

    public async Task InitializeAsync() => _host = await PersistenceHost.CreateSqliteAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private static DeliveryLog Log(string project, string recipient, DateTimeOffset at, DeliveryStatus status = DeliveryStatus.Sent, ChannelType channel = ChannelType.Email) =>
        DeliveryLog.Create(project, channel, recipient, status, "answer", 10, at);

    private async Task AddAsync(params DeliveryLog[] logs)
    {
        using var scope = _host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDeliveryLogRepository>();
        foreach (var log in logs) repository.Add(log);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private async Task<IReadOnlyList<DeliveryLog>> RecentAsync(string? project, int limit, ChannelType channel = ChannelType.Email)
    {
        using var scope = _host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDeliveryLogRepository>().ListRecentAsync(project, channel, limit);
    }

    [Fact]
    public async Task TheMostRecentComesFirst_WhateverTheOrderTheyWereWrittenIn()
    {
        await AddAsync(Log("p", "middle@x.co", T0.AddMinutes(5)), Log("p", "oldest@x.co", T0), Log("p", "newest@x.co", T0.AddMinutes(10)));

        var recent = await RecentAsync("p", 10);

        Assert.Equal(["newest@x.co", "middle@x.co", "oldest@x.co"], recent.Select(l => l.Recipient));
    }

    [Fact]
    public async Task TimesThatDifferByFractionsOfASecond_StillSortCorrectly()
    {
        await AddAsync(
            Log("p", "a@x.co", T0.AddMilliseconds(5)),
            Log("p", "b@x.co", T0),
            Log("p", "c@x.co", T0.AddMilliseconds(450)),
            Log("p", "d@x.co", T0.AddSeconds(1)));

        Assert.Equal(["d@x.co", "c@x.co", "a@x.co", "b@x.co"], (await RecentAsync("p", 10)).Select(l => l.Recipient));
    }

    [Fact]
    public async Task ItFiltersByProjectAndChannel_AndAppliesTheLimit()
    {
        await AddAsync(
            Log("a", "1@x.co", T0), Log("a", "2@x.co", T0.AddMinutes(1)), Log("a", "3@x.co", T0.AddMinutes(2)),
            Log("b", "4@x.co", T0.AddMinutes(3)), Log("a", "tg", T0.AddMinutes(4), channel: ChannelType.Telegram));

        Assert.Equal(["3@x.co", "2@x.co"], (await RecentAsync("a", 2)).Select(l => l.Recipient));
        Assert.Equal(["4@x.co", "3@x.co", "2@x.co", "1@x.co"], (await RecentAsync(null, 10)).Select(l => l.Recipient));   // every project, email only
        Assert.Equal(["tg"], (await RecentAsync("a", 10, ChannelType.Telegram)).Select(l => l.Recipient));
        Assert.Empty(await RecentAsync("nobody", 10));
    }

    [Fact]
    public async Task TheChatListing_MixesChatAndOlderTelegramRows_NeverEmail_NewestFirst_AndHonoursProjectAndLimit()
    {
        await AddAsync(
            Log("a", "chat-old", T0, channel: ChannelType.Chat),
            Log("a", "mail", T0.AddMinutes(1)),
            Log("a", "legacy-tg", T0.AddMinutes(2), channel: ChannelType.Telegram),
            Log("b", "chat-b", T0.AddMinutes(3), channel: ChannelType.Chat),
            Log("a", "chat-new", T0.AddMinutes(4), DeliveryStatus.Failed, ChannelType.Chat));
        using var scope = _host.CreateScope();
        var logs = scope.ServiceProvider.GetRequiredService<IDeliveryLogRepository>();
        ChannelType[] chat = [ChannelType.Chat, ChannelType.Telegram];

        Assert.Equal(["chat-new", "chat-b", "legacy-tg", "chat-old"], (await logs.ListRecentAsync(null, chat, 10)).Select(l => l.Recipient));
        Assert.Equal(["chat-new", "legacy-tg", "chat-old"], (await logs.ListRecentAsync("a", chat, 10)).Select(l => l.Recipient));
        Assert.Equal(["chat-new", "chat-b"], (await logs.ListRecentAsync(null, chat, 2)).Select(l => l.Recipient));
        Assert.Equal(["mail"], (await logs.ListRecentAsync("a", [ChannelType.Email], 10)).Select(l => l.Recipient));
        Assert.Equal(DeliveryStatus.Failed, (await logs.ListRecentAsync("a", chat, 1))[0].Status);
    }

    [Fact]
    public async Task AChatLog_RoundTrips_AsTheChatChannel()
    {
        await AddAsync(DeliveryLog.Create("p", ChannelType.Chat, "-1001", DeliveryStatus.Sent, "Telegram accepted (message 5)", 12, T0.AddTicks(987)));

        using var scope = _host.CreateScope();
        var log = Assert.Single(await scope.ServiceProvider.GetRequiredService<IDeliveryLogRepository>().ListRecentAsync("p", [ChannelType.Chat], 5));

        Assert.Equal((ChannelType.Chat, "-1001", 12, T0.AddTicks(987)), (log.ChannelType, log.Recipient, log.LatencyMs, log.CreatedAt));
    }

    [Fact]
    public async Task ALogRoundTrips_WithItsStatusResponseAndLatency()
    {
        await AddAsync(DeliveryLog.Create("p", ChannelType.Email, "who@x.co", DeliveryStatus.Failed, "The SMTP server rejected the login.", 1234, T0.AddTicks(1234567)));

        var log = Assert.Single(await RecentAsync("p", 5));

        Assert.Equal(("who@x.co", DeliveryStatus.Failed, "The SMTP server rejected the login.", 1234), (log.Recipient, log.Status, log.ProviderResponse, log.LatencyMs));
        Assert.Equal(T0.AddTicks(1234567), log.CreatedAt);   // every tick of the time survives
    }

    [Fact]
    public async Task EveryTime_IsStoredAsUtcText_WithAFixedLayout_AndReadsBackTheSameInstant()
    {
        var local = new DateTimeOffset(2026, 11, 11, 15, 30, 0, TimeSpan.FromHours(7));   // the same instant as 08:30 UTC
        var config = ChannelConfiguration.CreateFirstVersion("p", ChannelType.Email, ProviderType.Mock, "T", "{}", null, local);
        config.RecordTest(TestStatus.Success, local.AddMinutes(1));
        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            db.ChannelConfigurations.Add(config);
            await db.SaveChangesAsync();
        }

        using var read = _host.CreateScope();
        var context = read.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        var raw = await context.Database.SqlQueryRaw<string>(
            "SELECT created_at || '|' || last_tested_at || '|' || coalesce(activated_at, 'NULL') AS \"Value\" FROM channel_configurations").SingleAsync();
        var row = await context.ChannelConfigurations.AsNoTracking().SingleAsync();

        Assert.Equal("2026-11-11 08:30:00.0000000+00:00|2026-11-11 08:31:00.0000000+00:00|NULL", raw);
        Assert.Equal(local.UtcDateTime, row.CreatedAt.UtcDateTime);
        Assert.Equal(local.AddMinutes(1).UtcDateTime, row.LastTestedAt!.Value.UtcDateTime);
        Assert.Null(row.ActivatedAt);
    }

    [Fact]
    public async Task ARowWrittenBeforeTheFixedLayout_StillReadsBack()
    {
        using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        // What EF Core itself used to write for a UTC time: no fraction when it is zero.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO delivery_logs (id, project_code, channel_type, recipient, status, created_at) VALUES ({0}, 'old', 'Email', 'old@x.co', 'Sent', '2026-11-11 08:00:00+00:00')",
            Guid.NewGuid().ToString().ToUpperInvariant());

        var log = await db.DeliveryLogs.AsNoTracking().SingleAsync(l => l.ProjectCode == "old");

        Assert.Equal(T0, log.CreatedAt);
    }
}

/// <summary>The same on a real PostgreSQL server. Skipped without one.</summary>
public class PostgresDeliveryLogTests
{
    [PostgresFact]
    public async Task OnPostgres_TheMostRecentComesFirst_FilteredAndLimited()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        var t0 = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        using (var scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IDeliveryLogRepository>();
            repository.Add(DeliveryLog.Create("a", ChannelType.Email, "2@x.co", DeliveryStatus.Sent, "ok", 5, t0.AddMinutes(2)));
            repository.Add(DeliveryLog.Create("a", ChannelType.Email, "1@x.co", DeliveryStatus.Failed, "no", 6, t0.AddMinutes(1)));
            repository.Add(DeliveryLog.Create("b", ChannelType.Email, "3@x.co", DeliveryStatus.Sent, "ok", 7, t0.AddMinutes(3)));
            repository.Add(DeliveryLog.Create("a", ChannelType.Telegram, "tg", DeliveryStatus.Sent, "ok", 8, t0.AddMinutes(4)));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        using var read = host.CreateScope();
        var logs = read.ServiceProvider.GetRequiredService<IDeliveryLogRepository>();
        Assert.Equal(["3@x.co", "2@x.co", "1@x.co"], (await logs.ListRecentAsync(null, ChannelType.Email, 10)).Select(l => l.Recipient));
        Assert.Equal(["2@x.co", "1@x.co"], (await logs.ListRecentAsync("a", ChannelType.Email, 10)).Select(l => l.Recipient));
        Assert.Equal(["3@x.co"], (await logs.ListRecentAsync(null, ChannelType.Email, 1)).Select(l => l.Recipient));
        var first = (await logs.ListRecentAsync("a", ChannelType.Email, 10))[0];
        Assert.Equal((DeliveryStatus.Sent, 5, t0.AddMinutes(2)), (first.Status, first.LatencyMs, first.CreatedAt));
    }

    [PostgresFact]
    public async Task OnPostgres_TheChatListing_MixesChatAndOlderTelegramRows_NeverEmail()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        var t0 = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        using (var scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IDeliveryLogRepository>();
            repository.Add(DeliveryLog.Create("a", ChannelType.Chat, "chat-1", DeliveryStatus.Sent, "ok", 5, t0));
            repository.Add(DeliveryLog.Create("a", ChannelType.Email, "mail", DeliveryStatus.Sent, "ok", 5, t0.AddMinutes(1)));
            repository.Add(DeliveryLog.Create("a", ChannelType.Telegram, "legacy", DeliveryStatus.Sent, "ok", 5, t0.AddMinutes(2)));
            repository.Add(DeliveryLog.Create("b", ChannelType.Chat, "chat-b", DeliveryStatus.Failed, "no", 5, t0.AddMinutes(3)));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        using var read = host.CreateScope();
        var logs = read.ServiceProvider.GetRequiredService<IDeliveryLogRepository>();
        ChannelType[] chat = [ChannelType.Chat, ChannelType.Telegram];
        Assert.Equal(["chat-b", "legacy", "chat-1"], (await logs.ListRecentAsync(null, chat, 10)).Select(l => l.Recipient));
        Assert.Equal(["legacy", "chat-1"], (await logs.ListRecentAsync("a", chat, 10)).Select(l => l.Recipient));
        Assert.Equal(["chat-b"], (await logs.ListRecentAsync(null, chat, 1)).Select(l => l.Recipient));
    }
}
