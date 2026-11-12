using System.Net;
using System.Net.Http.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Email;
using AppInHouse.SharedServices.IntegrationTests.Support;
using AppInHouse.SharedServices.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AppInHouse.SharedServices.IntegrationTests.Support.PlatformApi;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>The same host, but with waits between attempts that can be measured.</summary>
public sealed class MeasuredRetryApiFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
    {
        ["Jobs:RetryDelaysSeconds:0"] = "1",
        ["Jobs:RetryDelaysSeconds:1"] = "2",
        ["Jobs:RetryDelaysSeconds:2"] = "0",
    };
}

public class JobApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Dispatch = "/api/v1/notifications/dispatch";

    private static object QueuedRegistration(string to, string? project = null) => new
    {
        templateCode = "UserRegistered", recipientEmail = to, projectCode = project, queue = true,
        data = Data(new { UserName = "Lan", AppName = "App", ActionUrl = "https://app.example.com/v", ExpiryMinutes = 5 }),
    };

    private Task<BackgroundJobRecord> JobAsync(Guid id) => factory.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == id));

    private async Task<Guid> QueueAsync(HttpClient client, string to, string? project = null) =>
        (await ReadAsync(await client.PostAsJsonAsync(Dispatch, QueuedRegistration(to, project)))).GetProperty("jobIds")[0].GetGuid();

    // ------------------------------------------------------------------------------------------------ the queues

    [Fact]
    public async Task TheQueues_AreTheFive_EachWithItsCountsAndPurpose()
    {
        var admin = await AdminAsync(factory);

        var response = await admin.GetAsync("/api/v1/jobs/queues");
        var queues = (await ReadAsync(response)).EnumerateArray().ToList();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["email", "chat", "media-thumbnail", "outbox", "webhook"], queues.Select(q => q.GetProperty("name").GetString()));
        Assert.All(queues, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.GetProperty("purpose").GetString()));
            foreach (var count in new[] { "pending", "processing", "completedLast24h", "failed" }) Assert.True(q.GetProperty(count).GetInt32() >= 0);
        });
    }

    [Fact]
    public async Task OnlyThePlatform_SeesTheQueuesRetriesAndSchedules()
    {
        var project = await ProjectClientAsync(factory, NewProject(), ApiScopes.ConfigRead, ApiScopes.ConfigWrite);

        Assert.Equal(HttpStatusCode.Forbidden, (await project.GetAsync("/api/v1/jobs/queues")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await project.PostAsync("/api/v1/jobs/queues/email/retry-failed", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await project.GetAsync("/api/v1/jobs/schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await project.PostAsync("/api/v1/jobs/schedules/log-retention/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).GetAsync("/api/v1/jobs/queues")).StatusCode);
        var readOnly = factory.ClientWithKey((await factory.CreateKeyAsync(ApiScopes.ConfigRead)).PlainKey);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/v1/jobs/queues")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync("/api/v1/jobs/queues/email/retry-failed", null)).StatusCode);   // reading is not retrying
    }

    // ------------------------------------------------------------------------------------------------ retry, backoff, failure

    [Fact]
    public async Task AJobThatKeepsFailing_IsTriedFourTimes_ThenFailed_AndShownAsFailedInTheQueue()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateSendGridAsync(admin, project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend);
        var calls = 0;
        factory.SendGrid.Respond = _ =>
        {
            Interlocked.Increment(ref calls);
            return ServerError();
        };
        try
        {
            var id = await QueueAsync(client, NewRecipient());

            Assert.True(await UntilAsync(async () => (await JobAsync(id)).Status == JobStatus.Failed));

            var job = await JobAsync(id);
            Assert.Equal((4, 4, 4), (job.Attempts, job.MaxAttempts, calls));
            Assert.NotNull(job.LastError);
            Assert.DoesNotContain("SG-platform-tests-api-key", job.LastError);   // a failure never carries the provider key
            var email = (await ReadAsync(await admin.GetAsync("/api/v1/jobs/queues"))).EnumerateArray().Single(q => q.GetProperty("name").GetString() == "email");
            Assert.True(email.GetProperty("failed").GetInt32() >= 1);
            Assert.Equal("Degraded", email.GetProperty("status").GetString());
        }
        finally
        {
            factory.SendGrid.Respond = _ => Accepted();
            await admin.PostAsync("/api/v1/jobs/queues/email/retry-failed", null);
        }
    }

    [Fact]
    public async Task RetryFailed_PutsTheFailedJobsBack_AndOnceTheProviderWorksTheEmailGoesOut()
    {
        var project = NewProject();
        var to = NewRecipient();
        var admin = await AdminAsync(factory);
        await ActivateSendGridAsync(admin, project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend);
        var accepted = 0;
        factory.SendGrid.Respond = _ => ServerError();
        Guid id;
        try
        {
            id = await QueueAsync(client, to);
            Assert.True(await UntilAsync(async () => (await JobAsync(id)).Status == JobStatus.Failed));
        }
        finally
        {
            factory.SendGrid.Respond = request =>
            {
                if (request.Content?.ReadAsStringAsync().GetAwaiter().GetResult().Contains(to) == true) Interlocked.Increment(ref accepted);
                return Accepted();
            };
        }

        try
        {
            var retry = await admin.PostAsync("/api/v1/jobs/queues/email/retry-failed", null);
            var body = await ReadAsync(retry);

            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal("email", body.GetProperty("queue").GetString());
            Assert.True(body.GetProperty("requeued").GetInt32() >= 1);
            Assert.True(await UntilAsync(async () => (await JobAsync(id)).Status == JobStatus.Completed), "the retried job did not complete");
            Assert.Equal(1, accepted);
            Assert.Equal(1, (await JobAsync(id)).Attempts);   // a retry starts over with a fresh set of attempts
            Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.Action == "RetryFailedJobs" && a.ResourceId == "email")));
        }
        finally
        {
            factory.SendGrid.Respond = _ => Accepted();
        }
    }

    [Fact]
    public async Task AFailureThatWaitingCannotCure_FailsAtOnce_WithoutRetrying()
    {
        // The project has no email configuration (and neither has "default" in this host): trying again four times would change nothing.
        var project = NewProject();
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend);

        var id = await QueueAsync(client, NewRecipient());

        Assert.True(await UntilAsync(async () => (await JobAsync(id)).Status == JobStatus.Failed));
        var job = await JobAsync(id);
        Assert.Equal(1, job.Attempts);
        Assert.Contains("no active email configuration", job.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AQueuedEmailOfADeactivatedProject_FailsAtOnce_AndTheQuotaIsNotRetriedEither()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, project);
        await admin.PostAsJsonAsync("/api/v1/projects", new { projectCode = project, name = "Quota", dailyEmailQuota = 0 });
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend);

        var id = await QueueAsync(client, NewRecipient());

        Assert.True(await UntilAsync(async () => (await JobAsync(id)).Status == JobStatus.Failed));
        var job = await JobAsync(id);
        Assert.Equal(1, job.Attempts);
        Assert.Contains("quota", job.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryingAnUnknownQueue_Is404()
    {
        var admin = await AdminAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/jobs/queues/nope/retry-failed", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/jobs/queues/Email/retry-failed", null)).StatusCode);
    }

    [Fact]
    public async Task TheClaimOfAJob_IsAtomic_SoOneOfSixteenWorkersGetsIt_AndNoWorkerGetsAFinishedOrAFutureOne()
    {
        var job = BackgroundJobRecord.Create(JobQueues.Email, "send", "{}", 4, DateTimeOffset.UtcNow.AddSeconds(-1));
        var later = BackgroundJobRecord.Create(JobQueues.Email, "send", "{}", 4, DateTimeOffset.UtcNow);
        later.MarkAttemptFailed("blip", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var done = BackgroundJobRecord.Create(JobQueues.Email, "send", "{}", 4, DateTimeOffset.UtcNow.AddSeconds(-1));
        done.MarkCompleted(DateTimeOffset.UtcNow);
        await factory.WithDbAsync(async db =>
        {
            db.BackgroundJobs.AddRange(job, later, done);
            return await db.SaveChangesAsync();
        });

        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IBackgroundJobRepository>().ClaimAsync(job.Id, DateTimeOffset.UtcNow);
        }));

        Assert.Equal(1, claims.Count(c => c.Job is not null));
        var stored = await JobAsync(job.Id);
        Assert.Equal((JobStatus.Processing, 1), (stored.Status, stored.Attempts));

        using var scope2 = factory.Services.CreateScope();
        var repository = scope2.ServiceProvider.GetRequiredService<IBackgroundJobRepository>();
        var notYet = await repository.ClaimAsync(later.Id, DateTimeOffset.UtcNow);
        var finished = await repository.ClaimAsync(done.Id, DateTimeOffset.UtcNow);
        var again = await repository.ClaimAsync(job.Id, DateTimeOffset.UtcNow);
        Assert.Null(notYet.Job);
        Assert.NotNull(notYet.NotBefore);   // it is waiting: look again then
        Assert.True(notYet.NotBefore > DateTimeOffset.UtcNow.AddMinutes(59));
        Assert.Equal((null, null), (finished.Job, finished.NotBefore));
        Assert.Equal((null, null), (again.Job, again.NotBefore));
    }

    // ------------------------------------------------------------------------------------------------ schedules

    [Fact]
    public async Task TheSchedules_AreTheThreeTasks_WithTheirCronAndAFutureNextRun()
    {
        var admin = await AdminAsync(factory);

        var tasks = (await ReadAsync(await admin.GetAsync("/api/v1/jobs/schedules"))).EnumerateArray().ToList();

        Assert.Equal(["log-retention", "job-housekeeping", "metrics-aggregate"], tasks.Select(t => t.GetProperty("id").GetString()));
        Assert.Equal(["0 2 * * *", "30 2 * * *", "*/5 * * * *"], tasks.Select(t => t.GetProperty("cron").GetString()));
        Assert.All(tasks, t =>
        {
            var next = t.GetProperty("nextRunAt").GetDateTimeOffset();
            Assert.True(next > DateTimeOffset.UtcNow.AddSeconds(-1));
            Assert.Equal(0, next.Second);
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("description").GetString()));
        });
        Assert.Equal(0, tasks[2].GetProperty("nextRunAt").GetDateTimeOffset().Minute % 5);
    }

    [Fact]
    public async Task RunningTheRetention_RemovesTheOldLogs_KeepsTheRecentOnesAndNeverTouchesTheAuditLog()
    {
        var project = NewProject();
        var old = DateTimeOffset.UtcNow.AddDays(-120);
        var recent = DateTimeOffset.UtcNow.AddDays(-10);
        await factory.WithDbAsync(async db =>
        {
            db.DeliveryLogs.AddRange(DeliveryLog.Create(project, ChannelType.Email, "old@example.com", DeliveryStatus.Sent, null, 5, old), DeliveryLog.Create(project, ChannelType.Email, "recent@example.com", DeliveryStatus.Sent, null, 5, recent));
            db.MediaLogs.AddRange(MediaLog.Create(project, null, "Upload", "Success", null, old), MediaLog.Create(project, null, "Upload", "Success", null, recent));
            db.SystemLogs.AddRange(SystemLog.Create("Warning", project, "old", null, old), SystemLog.Create("Warning", project, "recent", null, recent));
            db.NotificationLogs.AddRange(NotificationLog.Create(project, "UserRegistered", "Email", "Sent", null, 5, old), NotificationLog.Create(project, "UserRegistered", "Email", "Sent", null, 5, recent));
            db.AuditLogs.Add(AuditLog.Create("tests", "OldAudit", "Thing", project, null, old));
            return await db.SaveChangesAsync();
        });
        var admin = await AdminAsync(factory);

        var response = await admin.PostAsync("/api/v1/jobs/schedules/log-retention/run", null);
        var task = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("Success:", task.GetProperty("lastStatus").GetString());
        Assert.Equal(1, task.GetProperty("runs").GetInt32());
        Assert.Equal(["recent@example.com"], await factory.WithDbAsync(db => db.DeliveryLogs.Where(l => l.ProjectCode == project).Select(l => l.Recipient).ToListAsync()));
        Assert.Equal(1, await factory.WithDbAsync(db => db.MediaLogs.CountAsync(l => l.ProjectCode == project)));
        Assert.Equal(["recent"], await factory.WithDbAsync(db => db.SystemLogs.Where(l => l.Category == project).Select(l => l.Message).ToListAsync()));
        Assert.Equal(1, await factory.WithDbAsync(db => db.NotificationLogs.CountAsync(l => l.ProjectCode == project)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == "OldAudit")));
    }

    [Fact]
    public async Task RunningTheHousekeeping_RemovesFinishedJobsAndOldInboundCalls_ButNotFailedJobsOrRecentOnes()
    {
        var now = DateTimeOffset.UtcNow;
        var oldDone = BackgroundJobRecord.Create(JobQueues.Outbox, "event", "{}", 4, now.AddDays(-30));
        oldDone.MarkCompleted(now.AddDays(-30));
        var recentDone = BackgroundJobRecord.Create(JobQueues.Outbox, "event", "{}", 4, now.AddDays(-1));
        recentDone.MarkCompleted(now.AddDays(-1));
        var oldFailed = BackgroundJobRecord.Create(JobQueues.Outbox, "event", "{}", 1, now.AddDays(-30));
        oldFailed.MarkAttemptFailed("down", now.AddDays(-30), now.AddDays(-30), permanent: true);
        var source = "github";
        var oldCall = InboundWebhookLog.Accepted(source, "old-body", 8, now.AddDays(-200));
        var newCall = InboundWebhookLog.Accepted(source, "new-body", 8, now.AddDays(-2));
        await factory.WithDbAsync(async db =>
        {
            db.BackgroundJobs.AddRange(oldDone, recentDone, oldFailed);
            db.InboundWebhookLogs.AddRange(oldCall, newCall);
            return await db.SaveChangesAsync();
        });
        var admin = await AdminAsync(factory);

        var task = await ReadAsync(await admin.PostAsync("/api/v1/jobs/schedules/job-housekeeping/run", null));

        Assert.StartsWith("Success:", task.GetProperty("lastStatus").GetString());
        Assert.False(await factory.WithDbAsync(db => db.BackgroundJobs.AnyAsync(j => j.Id == oldDone.Id)));
        Assert.True(await factory.WithDbAsync(db => db.BackgroundJobs.AnyAsync(j => j.Id == recentDone.Id)));
        Assert.True(await factory.WithDbAsync(db => db.BackgroundJobs.AnyAsync(j => j.Id == oldFailed.Id)));   // a failed job waits for an administrator
        Assert.False(await factory.WithDbAsync(db => db.InboundWebhookLogs.AnyAsync(l => l.Id == oldCall.Id)));
        Assert.True(await factory.WithDbAsync(db => db.InboundWebhookLogs.AnyAsync(l => l.Id == newCall.Id)));
    }

    [Fact]
    public async Task TheMetricsTask_CanBeRunByHand_AndAnUnknownTaskIs404()
    {
        var admin = await AdminAsync(factory);

        var run = await ReadAsync(await admin.PostAsync("/api/v1/jobs/schedules/metrics-aggregate/run", null));

        Assert.Matches(@"^Success: \d of 8 services healthy\.$", run.GetProperty("lastStatus").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/jobs/schedules/nope/run", null)).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ thumbnails

    [Fact]
    public async Task ARebuildOfAThumbnail_IsQueued_AndTheMediaThumbnailWorkerWritesItAgain()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateAsync(admin, project, "Storage", "LocalDisk", new { rootPath = "uploads" });
        var client = await ProjectClientAsync(factory, project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()) { Headers = { ContentType = new("image/png") } }, "file", "p.png" } };
        var uploaded = await ReadAsync(await client.PostAsync("/api/v1/storage/upload", form));
        var id = uploaded.GetProperty("file").GetProperty("id").GetGuid();
        var thumbnailPath = await factory.WithDbAsync(async db => (await db.StoredFiles.SingleAsync(f => f.Id == id)).ThumbnailObjectKey);
        var onDisk = Directory.EnumerateFiles(factory.StoragePath, Path.GetFileName(thumbnailPath), SearchOption.AllDirectories).Single();
        File.Delete(onDisk);   // the thumbnail is lost
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/storage/files/{id}/thumbnail")).StatusCode);

        var queued = await client.PostAsync($"/api/v1/storage/files/{id}/thumbnail", null);
        var jobId = (await ReadAsync(queued)).GetProperty("jobId").GetGuid();

        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.True(await UntilAsync(async () => (await JobAsync(jobId)).Status == JobStatus.Completed));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/storage/files/{id}/thumbnail")).StatusCode);
        Assert.True(await factory.WithDbAsync(db => db.MediaLogs.AnyAsync(l => l.FileId == id && l.Action == "Thumbnail")));
    }

    [Fact]
    public async Task ARebuildNeedsAnImageOfYourOwnProject()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateAsync(admin, project, "Storage", "LocalDisk", new { rootPath = "uploads" });
        var owner = await ProjectClientAsync(factory, project, ApiScopes.StorageUpload, ApiScopes.StorageRead);
        var stranger = await ProjectClientAsync(factory, NewProject(), ApiScopes.StorageUpload);
        using var pdf = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Pdf()) { Headers = { ContentType = new("application/pdf") } }, "file", "d.pdf" } };
        var pdfId = (await ReadAsync(await owner.PostAsync("/api/v1/storage/upload", pdf))).GetProperty("file").GetProperty("id").GetGuid();
        using var png = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()) { Headers = { ContentType = new("image/png") } }, "file", "p.png" } };
        var pngId = (await ReadAsync(await owner.PostAsync("/api/v1/storage/upload", png))).GetProperty("file").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/v1/storage/files/{pdfId}/thumbnail", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsync($"/api/v1/storage/files/{pngId}/thumbnail", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/v1/storage/files/{Guid.NewGuid()}/thumbnail", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await ProjectClientAsync(factory, project, ApiScopes.StorageRead)).PostAsync($"/api/v1/storage/files/{pngId}/thumbnail", null)).StatusCode);
    }
}

public class JobEngineRestartTests
{
    [Fact]
    public async Task AJobThatWaitedInTheDatabase_IsPickedUpWhenTheServiceStarts()
    {
        var project = NewProject();
        var to = NewRecipient();
        using var first = new ApiFactory();
        await ActivateMockEmailAsync(await AdminAsync(first), project);
        var job = BackgroundJobRecord.Create(JobQueues.Email, "send", JobJson.Serialize(new EmailJobPayload(project, to, "Hello", "<p>Hi</p>", "Hi")), 4, DateTimeOffset.UtcNow);
        await first.WithDbAsync(async db =>
        {
            db.BackgroundJobs.Add(job);
            return await db.SaveChangesAsync();
        });
        await Task.Delay(700);   // nobody woke the worker: the first host does not look at the table again
        Assert.Equal(JobStatus.Pending, (await first.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id))).Status);

        using var second = first.WithWebHostBuilder(_ => { });
        _ = second.Server;   // starts the host, and with it the recovery of what waits
        var mailbox = second.Services.GetServices<IEmailProvider>().OfType<MockEmailProvider>().Single();

        Assert.True(await UntilAsync(() => mailbox.SentMessages.Any(m => m.To == to)), "the restarted service did not send the waiting email");
        Assert.True(await UntilAsync(async () => (await first.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id))).Status == JobStatus.Completed));
    }

    [Fact]
    public async Task AJobCutOffInTheMiddle_IsDoneAgainAtTheNextStart_WithItsInterruptedAttemptStillCounted()
    {
        var project = NewProject();
        var to = NewRecipient();
        using var first = new ApiFactory();
        await ActivateMockEmailAsync(await AdminAsync(first), project);
        var job = BackgroundJobRecord.Create(JobQueues.Email, "send", JobJson.Serialize(new EmailJobPayload(project, to, "Hello", "<p>Hi</p>", "Hi")), 4, DateTimeOffset.UtcNow.AddSeconds(-1));
        await first.WithDbAsync(async db =>
        {
            db.BackgroundJobs.Add(job);
            return await db.SaveChangesAsync();
        });
        using (var scope = first.Services.CreateScope())
            Assert.NotNull((await scope.ServiceProvider.GetRequiredService<IBackgroundJobRepository>().ClaimAsync(job.Id, DateTimeOffset.UtcNow)).Job);   // taken, then the service "died"
        Assert.Equal(JobStatus.Processing, (await first.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id))).Status);

        using var second = first.WithWebHostBuilder(_ => { });
        _ = second.Server;

        Assert.True(await UntilAsync(async () => (await first.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id))).Status == JobStatus.Completed));
        var done = await first.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id));
        Assert.Equal(2, done.Attempts);
        Assert.Contains(second.Services.GetServices<IEmailProvider>().OfType<MockEmailProvider>().Single().SentMessages, m => m.To == to);
    }
}

public class JobBackoffTests(MeasuredRetryApiFactory factory) : IClassFixture<MeasuredRetryApiFactory>
{
    [Fact]
    public async Task TheWaitsBetweenAttempts_AreTheConfiguredOnes_AndTheJobFailsAfterTheLast()
    {
        var project = NewProject();
        var admin = await AdminAsync(factory);
        await ActivateSendGridAsync(admin, project);
        var client = await ProjectClientAsync(factory, project, ApiScopes.EmailSend);
        var times = new List<DateTime>();
        factory.SendGrid.Respond = _ =>
        {
            lock (times) times.Add(DateTime.UtcNow);
            return ServerError();
        };

        var body = await ReadAsync(await client.PostAsJsonAsync("/api/v1/notifications/dispatch", new
        {
            templateCode = "UserRegistered", recipientEmail = NewRecipient(), queue = true,
            data = Data(new { UserName = "Lan", AppName = "App", ActionUrl = "https://a.example.com", ExpiryMinutes = 1 }),
        }));
        var id = body.GetProperty("jobIds")[0].GetGuid();

        Assert.True(await UntilAsync(async () => (await factory.WithDbAsync(db => db.BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == id))).Status == JobStatus.Failed, seconds: 20));

        Assert.Equal(4, times.Count);
        var gaps = times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
        Assert.InRange(gaps[0], 0.9, 3.0);    // waited 1 s
        Assert.InRange(gaps[1], 1.9, 4.0);    // waited 2 s
        Assert.InRange(gaps[2], 0.0, 0.9);    // waited 0 s
    }
}
