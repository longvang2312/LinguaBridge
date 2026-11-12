using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using AppInHouse.SharedServices.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>
/// The platform's stores and queries, once on SQLite (development and tests) and once on PostgreSQL (production): the same checks, so
/// anything that one provider translates differently from the other (time comparisons, group counts, updates in one statement) shows up.
/// </summary>
public class PlatformPersistenceTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------------------------------------ seeding

    private static async Task CheckSeedingAsync(PersistenceHost host)
    {
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            Assert.Equal(["PasswordReset", "PaymentSuccess", "SecurityAlert", "SystemMaintenance", "UserRegistered"], await db.NotificationTemplates.Select(t => t.Code).OrderBy(c => c).ToListAsync());
            Assert.Equal(["crm", "default", "identity", "linguabridge"], await db.ProjectTenants.Select(p => p.ProjectCode).OrderBy(c => c).ToListAsync());
            var crm = await db.ProjectTenants.SingleAsync(p => p.ProjectCode == "crm");
            Assert.Equal((ProjectEnvironment.Staging, 1000, 500), (crm.Environment, crm.DailyEmailQuota, crm.DailyChatQuota));
        }

        // Somebody changes a project and a template; starting again must neither duplicate nor overwrite.
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            (await db.ProjectTenants.SingleAsync(p => p.ProjectCode == "crm")).Update("CRM renamed", "x", ProjectEnvironment.Production, 7, null, null, true);
            (await db.NotificationTemplates.SingleAsync(t => t.Code == "UserRegistered")).SetActive(false);
            await db.SaveChangesAsync();
        }

        await host.Services.InitializeSharedServicesDatabaseAsync();
        await host.Services.InitializeSharedServicesDatabaseAsync();

        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            Assert.Equal(5, await db.NotificationTemplates.CountAsync());
            Assert.Equal(4, await db.ProjectTenants.CountAsync());
            var crm = await db.ProjectTenants.SingleAsync(p => p.ProjectCode == "crm");
            Assert.Equal(("CRM renamed", ProjectEnvironment.Production, 7, (int?)null), (crm.Name, crm.Environment, crm.DailyEmailQuota, crm.DailyChatQuota));
            Assert.False((await db.NotificationTemplates.SingleAsync(t => t.Code == "UserRegistered")).IsActive);
        }
    }

    private static async Task CheckUniqueCodesAsync(PersistenceHost host)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        db.ProjectTenants.Add(ProjectTenant.Create("identity", "Duplicate", null, ProjectEnvironment.Staging, null, null, null, Day));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        using var scope2 = host.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        db2.NotificationTemplates.Add(NotificationTemplate.Create("PasswordReset", "Dup", "s", "b", NotificationChannel.Email, NotificationPriority.Low, Day));
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task Sqlite_SeedsTheTemplatesAndProjectsOnce_AndNeverOverwritesAChange()
    {
        await using var host = await PersistenceHost.CreateSqliteAsync();
        await CheckSeedingAsync(host);
    }

    [PostgresFact]
    public async Task Postgres_SeedsTheTemplatesAndProjectsOnce_AndNeverOverwritesAChange()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckSeedingAsync(host);
    }

    [Fact]
    public async Task Sqlite_AProjectCodeAndATemplateCode_AreUnique() => await CheckUniqueCodesAsync(await PersistenceHost.CreateSqliteAsync());

    [PostgresFact]
    public async Task Postgres_AProjectCodeAndATemplateCode_AreUnique()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckUniqueCodesAsync(host);
    }

    // ------------------------------------------------------------------------------------------------ usage counts

    private static async Task CheckUsageAsync(PersistenceHost host)
    {
        var project = "usage-" + Guid.NewGuid().ToString("N")[..6];
        var from = Day;
        var to = Day.AddDays(1);
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            db.DeliveryLogs.AddRange(
                DeliveryLog.Create(project, ChannelType.Email, "a@example.com", DeliveryStatus.Sent, null, 1, from),                       // first instant of the day: counted
                DeliveryLog.Create(project, ChannelType.Email, "b@example.com", DeliveryStatus.Sent, null, 1, to.AddTicks(-1)),             // last instant: counted
                DeliveryLog.Create(project, ChannelType.Email, "c@example.com", DeliveryStatus.Sent, null, 1, to),                          // next day: not counted
                DeliveryLog.Create(project, ChannelType.Email, "d@example.com", DeliveryStatus.Sent, null, 1, from.AddTicks(-1)),           // the day before: not counted
                DeliveryLog.Create(project, ChannelType.Email, "e@example.com", DeliveryStatus.Failed, null, 1, from.AddHours(5)),
                DeliveryLog.Create(project, ChannelType.Chat, "1", DeliveryStatus.Sent, null, 1, from.AddHours(6)),
                DeliveryLog.Create(project, ChannelType.Telegram, "2", DeliveryStatus.Sent, null, 1, from.AddHours(7)),
                DeliveryLog.Create("someone-else", ChannelType.Email, "f@example.com", DeliveryStatus.Sent, null, 1, from.AddHours(8)));
            await db.SaveChangesAsync();
        }

        using var queries = host.CreateScope();
        var usage = queries.ServiceProvider.GetRequiredService<IUsageRepository>();
        Assert.Equal(2, await usage.CountDeliveriesAsync(project, [ChannelType.Email], DeliveryStatus.Sent, from, to));
        Assert.Equal(1, await usage.CountDeliveriesAsync(project, [ChannelType.Email], DeliveryStatus.Failed, from, to));
        Assert.Equal(3, await usage.CountDeliveriesAsync(project, [ChannelType.Email], null, from, to));
        Assert.Equal(2, await usage.CountDeliveriesAsync(project, [ChannelType.Chat, ChannelType.Telegram], DeliveryStatus.Sent, from, to));
        Assert.Equal(1, await usage.CountDeliveriesAsync(project, [ChannelType.Telegram], DeliveryStatus.Sent, from, to));
        Assert.True(await usage.CountDeliveriesAsync(null, [ChannelType.Email], DeliveryStatus.Sent, from, to) >= 3);   // every project, here at least ours and the other
        Assert.Equal(1, await usage.CountDeliveriesAsync("someone-else", [ChannelType.Email], DeliveryStatus.Sent, from, to));
    }

    private static async Task CheckOtherCountsAsync(PersistenceHost host)
    {
        var project = "counts-" + Guid.NewGuid().ToString("N")[..6];
        var now = DateTimeOffset.UtcNow;
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            var config = ChannelConfiguration.CreateFirstVersion(project, ChannelType.Storage, ProviderType.Mock, "S", "{}", null, now);
            db.ChannelConfigurations.Add(config);
            db.StoredFiles.AddRange(
                StoredFile.Create(project, config.Id, ProviderType.Mock, "a.png", "image/png", 1000, new string('a', 64), false, now),
                StoredFile.Create(project, config.Id, ProviderType.Mock, "b.png", "image/png", 2500, new string('b', 64), false, now));
            db.MediaLogs.AddRange(MediaLog.Create(project, null, "Upload", "Success", null, now), MediaLog.Create(project, null, "Upload", "Failed", "x", now), MediaLog.Create(project, null, "Upload", "Success", null, now.AddDays(-3)));
            db.InboundWebhookLogs.AddRange(InboundWebhookLog.Accepted("github", "{}", 2, now), InboundWebhookLog.Rejected("github", "bad", 2, now), InboundWebhookLog.Rejected("stripe", "bad", 2, now));
            db.NotificationLogs.AddRange(NotificationLog.Create(project, "T", "Email", "Sent", null, 1, now), NotificationLog.Create(project, "T", "Email", "Failed", null, 1, now));
            var (active, _) = ApiKey.Generate(project, "active", [ApiScopes.ConfigRead], null, now.AddDays(-10));
            var (revoked, _) = ApiKey.Generate(project, "revoked", [ApiScopes.ConfigRead], null, now.AddDays(-10));
            revoked.Revoke();
            var (expired, _) = ApiKey.Generate(project, "expired", [ApiScopes.ConfigRead], now.AddDays(-1), now.AddDays(-10));
            var (future, _) = ApiKey.Generate(project, "future", [ApiScopes.ConfigRead], now.AddDays(5), now.AddDays(-10));
            db.ApiKeys.AddRange(active, revoked, expired, future);
            await db.SaveChangesAsync();
        }

        using var queries = host.CreateScope();
        var usage = queries.ServiceProvider.GetRequiredService<IUsageRepository>();
        var since = now.AddHours(-1);
        Assert.Equal(3500, await usage.SumStorageBytesAsync(project));
        Assert.Equal(2, await usage.CountFilesAsync(project));
        Assert.Equal(0, await usage.SumStorageBytesAsync("nobody-" + Guid.NewGuid().ToString("N")[..6]));
        Assert.True(await usage.CountMediaLogsAsync("Failed", since) >= 1);
        Assert.True(await usage.CountMediaLogsAsync(null, since) >= 2);
        Assert.Equal(2, await usage.CountInboundAsync("github", null, since));
        Assert.Equal(1, await usage.CountInboundAsync("github", InboundWebhookStatus.Rejected, since));
        Assert.Equal(1, await usage.CountInboundAsync("stripe", InboundWebhookStatus.Rejected, since));
        Assert.NotNull(await usage.LastInboundAsync("github"));   // the accepted call; a refused one is not "received"
        Assert.Null(await usage.LastInboundAsync("telegram"));
        Assert.Equal(2, await usage.CountActiveApiKeysAsync(project, now));   // not the revoked one, not the expired one
        Assert.Equal(1, await usage.CountNotificationsAsync("Failed", since));
        Assert.Equal(2, await usage.CountNotificationsAsync(null, since));
        Assert.Equal(5, await usage.CountActiveTemplatesAsync());
        Assert.Equal(4, await usage.CountActiveProjectsAsync());
    }

    [Fact]
    public async Task Sqlite_TheCountsRespectTheHalfOpenDay_TheChannelsAndTheStatus()
    {
        await using var host = await PersistenceHost.CreateSqliteAsync();
        await CheckUsageAsync(host);
        await CheckOtherCountsAsync(host);
    }

    [PostgresFact]
    public async Task Postgres_TheCountsRespectTheHalfOpenDay_TheChannelsAndTheStatus()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckUsageAsync(host);
        await CheckOtherCountsAsync(host);
    }

    // ------------------------------------------------------------------------------------------------ jobs

    private static async Task CheckJobsAsync(PersistenceHost host)
    {
        var now = DateTimeOffset.UtcNow;
        var job = BackgroundJobRecord.Create(JobQueues.Email, "send", "{}", 4, now.AddSeconds(-1));
        var waiting = BackgroundJobRecord.Create(JobQueues.Email, "send", "{}", 4, now);
        waiting.MarkAttemptFailed("blip", now, now.AddHours(1));
        var done = BackgroundJobRecord.Create(JobQueues.Chat, "send", "{}", 4, now.AddMinutes(-5));
        done.MarkCompleted(now.AddMinutes(-4));
        var oldDone = BackgroundJobRecord.Create(JobQueues.Chat, "send", "{}", 4, now.AddDays(-5));
        oldDone.MarkCompleted(now.AddDays(-5));
        var failed = BackgroundJobRecord.Create(JobQueues.Webhook, "deliver", "{}", 1, now);
        failed.MarkAttemptFailed("down", now, now, permanent: true);
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            db.BackgroundJobs.AddRange(job, waiting, done, oldDone, failed);
            await db.SaveChangesAsync();
        }

        // Sixteen workers race for one job: exactly one wins it.
        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            using var scope = host.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IBackgroundJobRepository>().ClaimAsync(job.Id, DateTimeOffset.UtcNow);
        }));
        Assert.Equal(1, claims.Count(c => c.Job is not null));
        var won = claims.Single(c => c.Job is not null).Job!;
        Assert.Equal((JobStatus.Processing, 1), (won.Status, won.Attempts));

        using var queries = host.CreateScope();
        var repository = queries.ServiceProvider.GetRequiredService<IBackgroundJobRepository>();
        var notYet = await repository.ClaimAsync(waiting.Id, DateTimeOffset.UtcNow);
        Assert.Null(notYet.Job);
        Assert.True(notYet.NotBefore > DateTimeOffset.UtcNow.AddMinutes(59));
        Assert.Equal((null, null), ((await repository.ClaimAsync(done.Id, DateTimeOffset.UtcNow)).Job, (await repository.ClaimAsync(done.Id, DateTimeOffset.UtcNow)).NotBefore));

        var counts = (await repository.CountsAsync(now.AddHours(-24))).ToDictionary(c => c.Queue);
        Assert.True(counts["email"].Pending >= 1 && counts["email"].Processing >= 1);
        Assert.True(counts["chat"].CompletedSince >= 1);           // finished within the last 24 hours
        Assert.True(counts["webhook"].Failed >= 1);
        Assert.Contains(await repository.ListFailedAsync("webhook"), j => j.Id == failed.Id);
        Assert.DoesNotContain(await repository.ListFailedAsync("email"), j => j.Id == failed.Id);
        Assert.Contains(await repository.ListUnfinishedAsync(), j => j.Id == waiting.Id);
        Assert.DoesNotContain(await repository.ListUnfinishedAsync(), j => j.Id == done.Id);
    }

    private static async Task CheckRetentionAsync(PersistenceHost host)
    {
        var project = "ret-" + Guid.NewGuid().ToString("N")[..6];
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var oldDone = BackgroundJobRecord.Create(JobQueues.Outbox, "event", "{}", 4, cutoff.AddDays(-5));
        oldDone.MarkCompleted(cutoff.AddDays(-5));
        var newDone = BackgroundJobRecord.Create(JobQueues.Outbox, "event", "{}", 4, cutoff.AddDays(5));
        newDone.MarkCompleted(cutoff.AddDays(5));
        var oldFailed = BackgroundJobRecord.Create(JobQueues.Outbox, "event", "{}", 1, cutoff.AddDays(-5));
        oldFailed.MarkAttemptFailed("x", cutoff.AddDays(-5), cutoff.AddDays(-5), permanent: true);
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
            db.DeliveryLogs.AddRange(DeliveryLog.Create(project, ChannelType.Email, "old", DeliveryStatus.Sent, null, 1, cutoff.AddHours(-1)), DeliveryLog.Create(project, ChannelType.Email, "new", DeliveryStatus.Sent, null, 1, cutoff.AddHours(1)));
            db.AuditLogs.Add(AuditLog.Create("tests", "Ancient", "Thing", project, null, cutoff.AddYears(-3)));
            db.BackgroundJobs.AddRange(oldDone, newDone, oldFailed);
            await db.SaveChangesAsync();
        }

        using var scope2 = host.CreateScope();
        var retention = scope2.ServiceProvider.GetRequiredService<IRetentionRepository>();
        Assert.True(await retention.DeleteDeliveryLogsBeforeAsync(cutoff) >= 1);
        Assert.True(await retention.DeleteFinishedJobsBeforeAsync(cutoff) >= 1);

        using var check = host.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        Assert.Equal(["new"], await checkDb.DeliveryLogs.Where(l => l.ProjectCode == project).Select(l => l.Recipient).ToListAsync());
        Assert.False(await checkDb.BackgroundJobs.AnyAsync(j => j.Id == oldDone.Id));
        Assert.True(await checkDb.BackgroundJobs.AnyAsync(j => j.Id == newDone.Id));
        Assert.True(await checkDb.BackgroundJobs.AnyAsync(j => j.Id == oldFailed.Id));   // only finished-and-completed jobs are cleared
        Assert.True(await checkDb.AuditLogs.AnyAsync(l => l.Action == "Ancient"));
    }

    [Fact]
    public async Task Sqlite_AJobIsClaimedByExactlyOneWorker_TheQueueCountsAddUp_AndRetentionKeepsWhatMustStay()
    {
        await using var host = await PersistenceHost.CreateSqliteAsync();
        await CheckJobsAsync(host);
        await CheckRetentionAsync(host);
    }

    [PostgresFact]
    public async Task Postgres_AJobIsClaimedByExactlyOneWorker_TheQueueCountsAddUp_AndRetentionKeepsWhatMustStay()
    {
        await using var host = await PersistenceHost.CreatePostgresAsync(PostgresFactAttribute.ServerConnectionString!);
        await CheckJobsAsync(host);
        await CheckRetentionAsync(host);
    }

    [Fact]
    public async Task TheEncryptedSecretOfASubscription_SurvivesTheDatabase_AndIsNotTheKeyItself()
    {
        await using var host = await PersistenceHost.CreateSqliteAsync();
        using var scope = host.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var db = scope.ServiceProvider.GetRequiredService<SharedServicesDbContext>();
        const string secret = "whsec_roundtrip-0123456789";
        var subscription = WebhookSubscription.Create("crm", "App", "https://app.example.com/hook", protector.Protect(secret), secret, [WebhookEvents.EmailSent, WebhookEvents.FileUploaded], DateTimeOffset.UtcNow);
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync();

        using var other = host.CreateScope();
        var stored = await other.ServiceProvider.GetRequiredService<SharedServicesDbContext>().WebhookSubscriptions.SingleAsync(s => s.Id == subscription.Id);

        Assert.Equal(secret, other.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(stored.ProtectedSecret));
        Assert.DoesNotContain("roundtrip", stored.ProtectedSecret);
        Assert.Equal(["email.sent", "file.uploaded"], stored.EventList);
        Assert.True(stored.Wants("file.uploaded"));
    }
}
