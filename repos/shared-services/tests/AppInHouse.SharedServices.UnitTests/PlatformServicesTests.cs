using System.Text;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.SharedServices.UnitTests;

public class QuotaGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 30, 0, TimeSpan.Zero);

    private readonly FakeProjectRepository _projects = new();
    private readonly FakeUsageRepository _usage = new();

    private QuotaGuard Guard() => new(_projects, _usage, new FixedClock(Now));

    private ProjectTenant AddProject(int? emails = null, int? chats = null, long? bytes = null, bool active = true)
    {
        var project = ProjectTenant.Create("crm", "CRM", null, ProjectEnvironment.Staging, emails, chats, bytes, Now);
        if (!active) project.Update("CRM", null, ProjectEnvironment.Staging, emails, chats, bytes, isActive: false);
        _projects.Rows.Add(project);
        return project;
    }

    [Fact]
    public async Task AProjectNobodyRegistered_IsNotLimited()
    {
        await Guard().EnsureCanSendAsync("unknown", ChannelType.Email);
        await Guard().EnsureCanStoreAsync("unknown", 1_000_000_000);
    }

    [Fact]
    public async Task WithNoQuota_AnyAmountIsAllowed()
    {
        AddProject();
        _usage.Delivered = 1_000_000;
        _usage.StorageBytes = long.MaxValue / 2;

        await Guard().EnsureCanSendAsync("crm", ChannelType.Email);
        await Guard().EnsureCanSendAsync("crm", ChannelType.Chat);
        await Guard().EnsureCanStoreAsync("crm", 1_000);
    }

    [Fact]
    public async Task BelowTheDailyQuota_SendingIsAllowed_AtItItIsStopped()
    {
        AddProject(emails: 10);

        _usage.Delivered = 9;
        await Guard().EnsureCanSendAsync("crm", ChannelType.Email);

        _usage.Delivered = 10;
        var stop = await Assert.ThrowsAsync<QuotaExceededException>(() => Guard().EnsureCanSendAsync("crm", ChannelType.Email));
        Assert.Equal((QuotaKind.DailyEmail, 10L, 10L), (stop.Kind, stop.Limit, stop.Used));
    }

    [Fact]
    public async Task ADailyQuota_StartsAgainAtTheNextUtcMidnight_AndSaysSo()
    {
        AddProject(emails: 1);
        _usage.Delivered = 1;

        var stop = await Assert.ThrowsAsync<QuotaExceededException>(() => Guard().EnsureCanSendAsync("crm", ChannelType.Email));

        Assert.Equal(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), stop.ResetsAt);
        Assert.Contains("00:00 UTC", stop.Message);
    }

    [Fact]
    public async Task OnlyTheSuccessfulDeliveriesOfTodayInTheProjectsOwnNameAreCounted()
    {
        AddProject(emails: 10);

        await Guard().EnsureCanSendAsync("crm", ChannelType.Email);

        var query = Assert.Single(_usage.DeliveryQueries);
        Assert.Equal("crm", query.Project);
        Assert.Equal(DeliveryStatus.Sent, query.Status);
        Assert.Equal([ChannelType.Email], query.Channels);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero), query.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), query.To);
    }

    [Fact]
    public async Task TheChatQuota_CountsBothChatChannels_AndIsIndependentOfTheEmailOne()
    {
        AddProject(emails: 5, chats: 2);
        _usage.Deliveries = (channels, _) => channels.Contains(ChannelType.Email) ? 0 : 2;

        await Guard().EnsureCanSendAsync("crm", ChannelType.Email);
        var stop = await Assert.ThrowsAsync<QuotaExceededException>(() => Guard().EnsureCanSendAsync("crm", ChannelType.Chat));

        Assert.Equal(QuotaKind.DailyChat, stop.Kind);
        var chat = _usage.DeliveryQueries.Last();
        Assert.Contains(ChannelType.Chat, chat.Channels);
        Assert.Contains(ChannelType.Telegram, chat.Channels);   // configurations saved before "Chat" existed are chat too
    }

    [Fact]
    public async Task AQuotaOfZero_StopsEverythingFromTheFirstMessage()
    {
        AddProject(emails: 0);

        await Assert.ThrowsAsync<QuotaExceededException>(() => Guard().EnsureCanSendAsync("crm", ChannelType.Email));
    }

    [Fact]
    public async Task ADeactivatedProject_MayNeitherSendNorStore_WhateverItsQuotas()
    {
        AddProject(active: false);

        var send = await Assert.ThrowsAsync<ProjectInactiveException>(() => Guard().EnsureCanSendAsync("crm", ChannelType.Email));
        await Assert.ThrowsAsync<ProjectInactiveException>(() => Guard().EnsureCanStoreAsync("crm", 1));
        Assert.Equal("crm", send.ProjectCode);
    }

    [Theory]
    [InlineData(100, 50, 50, false)]    // exactly fits
    [InlineData(100, 50, 51, true)]
    [InlineData(100, 100, 1, true)]
    [InlineData(100, 100, 0, true)]     // asking only "is there room at all": none
    [InlineData(100, 99, 0, false)]
    [InlineData(100, 0, 100, false)]
    public async Task TheStorageQuota_IsCheckedAgainstWhatIsUsedPlusWhatComes(long quota, long used, long incoming, bool refused)
    {
        AddProject(bytes: quota);
        _usage.StorageBytes = used;

        if (refused)
        {
            var stop = await Assert.ThrowsAsync<QuotaExceededException>(() => Guard().EnsureCanStoreAsync("crm", incoming));
            Assert.Equal(QuotaKind.Storage, stop.Kind);
            Assert.Null(stop.ResetsAt);   // it only frees up when files are deleted
        }
        else
        {
            await Guard().EnsureCanStoreAsync("crm", incoming);
        }
    }

    [Fact]
    public void TheDayOfAnInstant_IsTheUtcDay_NotTheLocalOne()
    {
        var (from, to) = QuotaGuard.Today(new DateTimeOffset(2026, 11, 12, 23, 30, 0, TimeSpan.FromHours(-5)));   // already 04:30 UTC on the 13th

        Assert.Equal(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero), to);
    }
}

public class ProjectManagerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeProjectRepository _projects = new();
    private readonly FakeUsageRepository _usage = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeAuditLogWriter _audit = new();

    private ProjectManager Manager() => new(_projects, _usage, _unitOfWork, _audit, new FixedClock(Now));

    private static CreateProjectCommand Create(string code = "billing", string name = "Billing") =>
        new(code, name, "Invoices", ProjectEnvironment.Production, 100, 50, 1024);

    [Fact]
    public async Task ANewProject_IsStored_Audited_AndReturned()
    {
        var created = await Manager().CreateAsync(Create("  Billing  "));

        Assert.Equal("billing", created.ProjectCode);
        Assert.Equal(("Production", 100, 50, 1024L, true), (created.Environment, created.DailyEmailQuota, created.DailyChatQuota, created.StorageQuotaBytes, created.IsActive));
        Assert.Single(_projects.Rows);
        Assert.Equal("CreateProject", Assert.Single(_audit.Entries).Action);
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Space")]
    [InlineData("-leading")]
    [InlineData("this-code-is-way-too-long-to-be-a-project-code-0123456789012345")]
    public async Task ABadCode_IsRefused_WithTheFieldNamed(string code)
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() => Manager().CreateAsync(Create(code)));

        Assert.True(error.Errors.ContainsKey("ProjectCode"));
        Assert.Empty(_projects.Rows);
    }

    [Fact]
    public async Task ACodeThatExists_IsAConflict_EvenInAnotherCase()
    {
        await Manager().CreateAsync(Create("billing"));

        await Assert.ThrowsAsync<DomainException>(() => Manager().CreateAsync(Create("BILLING")));
        Assert.Single(_projects.Rows);
    }

    [Fact]
    public async Task ABadValue_IsAValidationError_NotAConflict()
    {
        var error = await Assert.ThrowsAsync<ValidationException>(() => Manager().CreateAsync(Create() with { DailyEmailQuota = -1 }));

        Assert.Contains("negative", error.Errors["Project"][0]);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task AnUpdate_ReplacesEverythingEditable_AndIsAudited()
    {
        await Manager().CreateAsync(Create());

        var updated = await Manager().UpdateAsync("billing", new UpdateProjectCommand("Billing 2", "New", ProjectEnvironment.Staging, null, 7, null, IsActive: false));

        Assert.Equal(("Billing 2", "Staging", (int?)null, (int?)7, false), (updated.Name, updated.Environment, updated.DailyEmailQuota, updated.DailyChatQuota, updated.IsActive));
        Assert.Equal(["CreateProject", "UpdateProject"], _audit.Entries.Select(e => e.Action));
    }

    [Fact]
    public async Task UpdatingAnUnknownProject_IsNotFound_AndThePlatformCannotBeSwitchedOff()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Manager().UpdateAsync("nope", new UpdateProjectCommand("X", null, ProjectEnvironment.Staging, null, null, null, true)));

        await Manager().CreateAsync(Create("default", "Platform"));
        var error = await Assert.ThrowsAsync<ValidationException>(() => Manager().UpdateAsync("default", new UpdateProjectCommand("Platform", null, ProjectEnvironment.Production, null, null, null, false)));
        Assert.Contains("platform", error.Errors["Project"][0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AKeyOfOneProject_SeesOnlyItsOwnProject_ThePlatformSeesAll_TheDefaultFirst()
    {
        await Manager().CreateAsync(Create("zeta", "Z"));
        await Manager().CreateAsync(Create("default", "Platform"));
        await Manager().CreateAsync(Create("alpha", "A"));

        Assert.Equal(["default", "alpha", "zeta"], (await Manager().ListAsync(null)).Select(p => p.ProjectCode));
        Assert.Equal(["alpha"], (await Manager().ListAsync("alpha")).Select(p => p.ProjectCode));
    }

    [Fact]
    public async Task TheUsage_ReportsTodaysDeliveries_TheStorage_AndTheQuotasNextToThem()
    {
        await Manager().CreateAsync(Create());
        _usage.Delivered = 3;
        _usage.StorageBytes = 512;
        _usage.Files = 4;
        _usage.ActiveKeys = 2;

        var usage = await Manager().GetUsageAsync("BILLING");

        Assert.Equal(("billing", new DateOnly(2026, 11, 12), 3, 100, 3, 50), (usage.ProjectCode, usage.Date, usage.EmailsSentToday, usage.DailyEmailQuota, usage.ChatSentToday, usage.DailyChatQuota));
        Assert.Equal((512L, 1024L, 4, 2), (usage.StorageBytes, usage.StorageQuotaBytes, usage.FileCount, usage.ActiveApiKeys));
        await Assert.ThrowsAsync<NotFoundException>(() => Manager().GetUsageAsync("nope"));
    }
}

public class JobQueueServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeBackgroundJobRepository _jobs = new();
    private readonly FakeJobChannels _channels = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeAuditLogWriter _audit = new();

    private JobQueueService Queue(JobSettings? settings = null) => new(_jobs, _channels, _unitOfWork, _audit, settings ?? new JobSettings(), new FixedClock(Now));

    [Fact]
    public async Task AJob_IsSavedBeforeItsWakeUpIsSent_SoAnItemNeverPointsAtNothing()
    {
        var savedWhenWritten = false;
        _channels.OnWrite = item => savedWhenWritten = _unitOfWork.Saves == 1 && _jobs.Rows.Any(j => j.Id == item.JobId);

        var id = await Queue().EnqueueAsync(JobQueues.Email, "send", new EmailJobPayload("crm", "a@b.co", "S", "<p>b</p>", "b"));

        Assert.True(savedWhenWritten);
        var item = Assert.Single(_channels.Written);
        Assert.Equal((id, JobQueues.Email), (item.JobId, item.Queue));
    }

    [Fact]
    public async Task TheJob_CarriesItsPayloadAsJson_AndTheConfiguredNumberOfAttempts()
    {
        await Queue(new JobSettings { RetryDelaysSeconds = [1, 2] }).EnqueueAsync(JobQueues.Chat, "send", new ChatJobPayload("crm", "42", "hello"));

        var job = Assert.Single(_jobs.Rows);
        Assert.Equal(3, job.MaxAttempts);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(new ChatJobPayload("crm", "42", "hello"), JobJson.Read<ChatJobPayload>(job));
    }

    [Fact]
    public async Task AnUnknownQueue_CannotBeEnqueued_AndNothingIsWritten()
    {
        await Assert.ThrowsAsync<DomainException>(() => Queue().EnqueueAsync("nope", "send", new { }));

        Assert.Empty(_jobs.Rows);
        Assert.Empty(_channels.Written);
    }

    [Fact]
    public async Task TheQueues_AreAlwaysTheFiveOfTheService_WithZerosWhereThereIsNothing()
    {
        _jobs.Counts.Add(new QueueCounts("email", 2, 1, 30, 4));

        var queues = await Queue().GetQueuesAsync();

        Assert.Equal(["email", "chat", "media-thumbnail", "outbox", "webhook"], queues.Select(q => q.Name));
        var email = queues[0];
        Assert.Equal((2, 1, 30, 4, "Degraded"), (email.Pending, email.Processing, email.CompletedLast24h, email.Failed, email.Status));
        Assert.Equal((0, 0, 0, 0, "Healthy"), (queues[1].Pending, queues[1].Processing, queues[1].CompletedLast24h, queues[1].Failed, queues[1].Status));
        Assert.All(queues, q => Assert.False(string.IsNullOrWhiteSpace(q.Purpose)));
    }

    [Fact]
    public async Task RetryingFailedJobs_PutsOnlyThatQueuesFailedJobsBack_WithFreshAttempts_AndWakesTheWorker()
    {
        var failedEmail = Failed(JobQueues.Email);
        var failedChat = Failed(JobQueues.Chat);
        var pendingEmail = BackgroundJobRecord.Create(JobQueues.Email, "send", "{}", 4, Now);
        _jobs.Rows.AddRange([failedEmail, failedChat, pendingEmail]);

        var result = await Queue().RetryFailedAsync(JobQueues.Email);

        Assert.Equal(new RetryFailedResult("email", 1), result);
        Assert.Equal((JobStatus.Pending, 0), (failedEmail.Status, failedEmail.Attempts));
        Assert.Equal(JobStatus.Failed, failedChat.Status);
        Assert.Equal(failedEmail.Id, Assert.Single(_channels.Written).JobId);
        Assert.Equal("RetryFailedJobs", Assert.Single(_audit.Entries).Action);
    }

    [Fact]
    public async Task RetryingWhereNothingFailed_DoesNothing_AndWritesNoAudit()
    {
        var result = await Queue().RetryFailedAsync(JobQueues.Webhook);

        Assert.Equal(0, result.Requeued);
        Assert.Empty(_channels.Written);
        Assert.Empty(_audit.Entries);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task RetryingAnUnknownQueue_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Queue().RetryFailedAsync("nope"));
    }

    private static BackgroundJobRecord Failed(string queue)
    {
        var job = BackgroundJobRecord.Create(queue, "send", "{}", 1, Now);
        typeof(BackgroundJobRecord).GetProperty(nameof(BackgroundJobRecord.Attempts))!.SetValue(job, 1);
        job.MarkAttemptFailed("down", Now, Now);
        return job;
    }
}

public class WebhookDelivererTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private const string Secret = "whsec_deliverer-test-secret-01";

    private readonly FakeWebhookSender _sender = new();

    private (WebhookDeliverer Deliverer, WebhookSubscription Subscription) Create(string url = "https://app.example.com/hook")
    {
        var protector = new FakeSecretProtector();
        var subscription = WebhookSubscription.Create("crm", "App", url, protector.Protect(Secret), Secret, [WebhookEvents.EmailSent], Now);
        return (new WebhookDeliverer(_sender, protector, new FixedClock(Now), NullLogger<WebhookDeliverer>.Instance), subscription);
    }

    [Fact]
    public async Task ThePost_CarriesTheSignatureOfExactlyTheBodyItSends_AndTheEventHeaders()
    {
        var (deliverer, subscription) = Create();
        const string body = """{"id":"e1","event":"email.sent"}""";

        var result = await deliverer.DeliverAsync(subscription, "email.sent", "e1", body, CancellationToken.None);

        Assert.True(result.Success);
        var post = Assert.Single(_sender.Posts);
        Assert.Equal(("https://app.example.com/hook", body), (post.Url, post.Body));
        Assert.Equal(WebhookSigner.Sign(Secret, body), post.Headers["X-AIH-Signature"]);
        Assert.True(WebhookSigner.IsValid(Secret, Encoding.UTF8.GetBytes(post.Body), post.Headers["X-AIH-Signature"]));
        Assert.Equal("email.sent", post.Headers["X-AIH-Event"]);
        Assert.Equal("e1", post.Headers["X-AIH-Delivery"]);
        Assert.Equal(Now.ToUnixTimeSeconds().ToString(), post.Headers["X-AIH-Timestamp"]);
    }

    [Fact]
    public async Task TheSecret_NeverTravels()
    {
        var (deliverer, subscription) = Create();

        await deliverer.DeliverAsync(subscription, "email.sent", "e1", "{}", CancellationToken.None);

        var post = Assert.Single(_sender.Posts);
        Assert.DoesNotContain(Secret, post.Body + string.Join(' ', post.Headers.Values));
    }

    [Theory]
    [InlineData(200, true, false)]
    [InlineData(204, true, false)]
    [InlineData(301, false, false)]    // no redirect is followed, so it is a failure; trying again might work
    [InlineData(400, false, true)]
    [InlineData(404, false, true)]
    [InlineData(410, false, true)]
    [InlineData(408, false, false)]
    [InlineData(429, false, false)]
    [InlineData(500, false, false)]
    [InlineData(503, false, false)]
    public async Task TheStatus_DecidesBetweenDoneRetryAndGiveUp(int status, bool success, bool permanent)
    {
        var (deliverer, subscription) = Create();
        _sender.Result = () => new WebhookHttpResult(status, null);

        var result = await deliverer.DeliverAsync(subscription, "email.sent", "e1", "{}", CancellationToken.None);

        Assert.Equal((success, permanent, status), (result.Success, result.Permanent, result.StatusCode));
    }

    [Fact]
    public async Task ANetworkFailure_IsRetried_AndATargetThePolicyForbidsIsNeverPosted()
    {
        var (deliverer, subscription) = Create();
        _sender.Result = () => new WebhookHttpResult(null, "The application did not answer in time.");
        var timeout = await deliverer.DeliverAsync(subscription, "email.sent", "e1", "{}", CancellationToken.None);

        _sender.Posts.Clear();
        _sender.Blocked = "10.0.0.5 is on a private network.";
        var blocked = await deliverer.DeliverAsync(subscription, "email.sent", "e2", "{}", CancellationToken.None);

        Assert.False(timeout.Permanent);
        Assert.Equal("The application did not answer in time.", timeout.Error);
        Assert.True(blocked.Permanent);
        Assert.Empty(_sender.Posts);
    }

    [Fact]
    public async Task ASecretThatCannotBeRead_IsAPermanentFailure_AndNothingIsPosted()
    {
        var (deliverer, subscription) = Create();
        var foreign = WebhookSubscription.Create("crm", "App", "https://app.example.com/hook", "not-protected-by-this-ring", Secret, [WebhookEvents.EmailSent], Now);

        var result = await deliverer.DeliverAsync(foreign, "email.sent", "e1", "{}", CancellationToken.None);

        Assert.True(result.Permanent);
        Assert.Empty(_sender.Posts);
        Assert.Equal(1, foreign.FailureCount);
        _ = subscription;
    }

    [Fact]
    public async Task EveryAttempt_IsCountedOnTheSubscription()
    {
        var (deliverer, subscription) = Create();

        await deliverer.DeliverAsync(subscription, "email.sent", "e1", "{}", CancellationToken.None);
        _sender.Result = () => new WebhookHttpResult(500, null);
        await deliverer.DeliverAsync(subscription, "email.sent", "e2", "{}", CancellationToken.None);

        Assert.Equal((1, 1, "500", (DateTimeOffset?)Now), (subscription.SuccessCount, subscription.FailureCount, subscription.LastStatus, subscription.LastDeliveryAt));
    }

    [Fact]
    public async Task AnExceptionFromTheSender_IsAFailureWithoutTheSecretInIt()
    {
        var (deliverer, subscription) = Create();
        var throwing = new ThrowingSender();
        var deliverer2 = new WebhookDeliverer(throwing, new FakeSecretProtector(), new FixedClock(Now), NullLogger<WebhookDeliverer>.Instance);

        var result = await deliverer2.DeliverAsync(subscription, "email.sent", "e1", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(Secret, result.Error);
        _ = deliverer;
    }

    private sealed class ThrowingSender : IWebhookSender
    {
        public Task<string?> CheckTargetAsync(string url, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<WebhookHttpResult> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection to https://app.example.com/hook failed; key=" + Secret);
    }
}

public class WebhookManagerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeWebhookRepository _repository = new();
    private readonly FakeWebhookSender _sender = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeAuditLogWriter _audit = new();
    private readonly FakeSecretProtector _protector = new();

    private WebhookManager Manager()
    {
        var clock = new FixedClock(Now);
        return new WebhookManager(_repository, _protector, _sender, new WebhookDeliverer(_sender, _protector, clock, NullLogger<WebhookDeliverer>.Instance), _unitOfWork, _audit, clock);
    }

    private static CreateWebhookSubscriptionCommand Command(string? secret = null, string url = "https://app.example.com/hook?token=TOKEN-IN-QUERY") =>
        new("CRM", "App", url, [WebhookEvents.EmailSent], secret);

    [Fact]
    public async Task ASubscription_GetsAGeneratedKey_ShownOnce_AndStoredOnlyProtected()
    {
        var created = await Manager().CreateAsync(Command());

        Assert.StartsWith("whsec_", created.SecretKey);
        Assert.True(created.SecretKey.Length >= 40);
        var stored = Assert.Single(_repository.Rows);
        Assert.NotEqual(created.SecretKey, stored.ProtectedSecret);
        Assert.DoesNotContain(created.SecretKey, stored.ProtectedSecret);
        Assert.Equal(created.SecretKey, _protector.Unprotect(stored.ProtectedSecret));
        Assert.Equal("crm", created.Subscription.ProjectCode);
    }

    [Fact]
    public async Task TwoGeneratedKeys_AreNeverTheSame()
    {
        var first = await Manager().CreateAsync(Command());
        var second = await Manager().CreateAsync(Command());

        Assert.NotEqual(first.SecretKey, second.SecretKey);
    }

    [Fact]
    public async Task TheListAndTheDto_ShowOnlyAMaskedKey()
    {
        var created = await Manager().CreateAsync(Command(secret: "my-own-signing-key-WXYZ"));

        var listed = Assert.Single(await Manager().ListAsync(null));

        Assert.Equal("••••••••WXYZ", listed.SecretMask);
        Assert.DoesNotContain("my-own-signing-key", System.Text.Json.JsonSerializer.Serialize(listed));
        Assert.Equal("my-own-signing-key-WXYZ", created.SecretKey);
    }

    [Fact]
    public async Task LoggingTheCreatedObject_DoesNotRevealTheKey()
    {
        var created = await Manager().CreateAsync(Command());

        Assert.DoesNotContain(created.SecretKey, created.ToString());
    }

    [Fact]
    public async Task TheAudit_NamesTheHostButNeverTheAddressOrTheKey()
    {
        var created = await Manager().CreateAsync(Command());

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("CreateWebhookSubscription", entry.Action);
        Assert.Contains("app.example.com", entry.DetailsJson);
        Assert.DoesNotContain("TOKEN-IN-QUERY", entry.DetailsJson);
        Assert.DoesNotContain(created.SecretKey, entry.DetailsJson);
    }

    [Fact]
    public async Task ATargetThePolicyForbids_IsRefused_AndNothingIsStored()
    {
        _sender.Blocked = "10.0.0.5 is on a private or loopback network, which this service is set not to contact.";

        var error = await Assert.ThrowsAsync<ValidationException>(() => Manager().CreateAsync(Command(url: "http://10.0.0.5/hook")));

        Assert.True(error.Errors.ContainsKey("TargetUrl"));
        Assert.Empty(_repository.Rows);
        Assert.Empty(_audit.Entries);
    }

    [Theory]
    [InlineData("ftp://app.example.com/hook")]
    [InlineData("not a url")]
    [InlineData("https://u:p@app.example.com/hook")]
    public async Task ABadAddress_IsAValidationError(string url)
    {
        await Assert.ThrowsAsync<ValidationException>(() => Manager().CreateAsync(Command(url: url)));
    }

    [Fact]
    public async Task AnOwnKeyThatIsTooShort_AndABadProjectCode_AreValidationErrors()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Manager().CreateAsync(Command(secret: "short")));
        await Assert.ThrowsAsync<ValidationException>(() => Manager().CreateAsync(Command() with { ProjectCode = "Bad Code" }));
    }

    [Fact]
    public async Task ADeletedSubscription_IsGone_AndAnUnknownOneIsNotFound()
    {
        var created = await Manager().CreateAsync(Command());

        await Manager().DeleteAsync(created.Subscription.Id);

        Assert.Empty(_repository.Rows);
        Assert.Equal("DeleteWebhookSubscription", _audit.Entries.Last().Action);
        await Assert.ThrowsAsync<NotFoundException>(() => Manager().DeleteAsync(created.Subscription.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => Manager().GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task TheTest_PostsATestEventSignedWithTheSubscriptionsKey_AndReportsTheAnswer()
    {
        var created = await Manager().CreateAsync(Command(secret: "my-own-signing-key-WXYZ"));
        _sender.Result = () => new WebhookHttpResult(503, null);

        var result = await Manager().TestAsync(created.Subscription.Id);

        Assert.False(result.Success);
        Assert.Equal(503, result.StatusCode);
        var post = Assert.Single(_sender.Posts);
        Assert.Equal("webhook.test", post.Headers["X-AIH-Event"]);
        Assert.True(WebhookSigner.IsValid("my-own-signing-key-WXYZ", Encoding.UTF8.GetBytes(post.Body), post.Headers["X-AIH-Signature"]));
        using var json = JsonDocument.Parse(post.Body);
        Assert.Equal("webhook.test", json.RootElement.GetProperty("event").GetString());
        Assert.Equal("crm", json.RootElement.GetProperty("project").GetString());
        Assert.Equal(1, _repository.Rows[0].FailureCount);
    }

    [Fact]
    public void TheEnvelope_HasTheIdTheEventTheProjectTheTimeAndTheData()
    {
        var json = WebhookEnvelope.Build("e-1", "file.uploaded", "crm", Now, new { fileId = 7, name = "a.png" });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(("e-1", "file.uploaded", "crm"), (root.GetProperty("id").GetString(), root.GetProperty("event").GetString(), root.GetProperty("project").GetString()));
        Assert.Equal(Now, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(7, root.GetProperty("data").GetProperty("fileId").GetInt32());
    }
}

public class EventPublishingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeWebhookRepository _repository = new();
    private readonly FakeJobQueue _jobs = new();

    private OutboxEventPublisher Publisher() => new(_repository, _jobs, new FixedClock(Now), NullLogger<OutboxEventPublisher>.Instance);

    private WebhookSubscription Add(string project, string[] events, bool active = true)
    {
        var subscription = WebhookSubscription.Create(project, "App", "https://app.example.com/hook", "p", new string('s', 16), events, Now);
        subscription.SetActive(active);
        _repository.Rows.Add(subscription);
        return subscription;
    }

    [Fact]
    public async Task WithNobodySubscribed_NoJobIsMade()
    {
        Add("crm", [WebhookEvents.ChatSent]);

        await Publisher().PublishAsync(WebhookEvents.EmailSent, "crm", new { });

        Assert.Empty(_jobs.Jobs);
    }

    [Fact]
    public async Task WithASubscriber_OneOutboxJobHoldsTheEnvelope()
    {
        Add("crm", [WebhookEvents.EmailSent]);

        await Publisher().PublishAsync(WebhookEvents.EmailSent, "crm", new { recipient = "a@b.co" });

        var job = Assert.Single(_jobs.Jobs);
        Assert.Equal((JobQueues.Outbox, "event"), (job.Queue, job.Type));
        var payload = Assert.IsType<OutboxEventPayload>(job.Payload);
        Assert.Equal(("email.sent", "crm"), (payload.Event, payload.Project));
        using var body = JsonDocument.Parse(payload.Body);
        Assert.Equal("a@b.co", body.RootElement.GetProperty("data").GetProperty("recipient").GetString());
        Assert.Equal(payload.EventId, body.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task APlatformSubscriberHearsEveryProject_AProjectSubscriberOnlyItsOwn_AnInactiveOneNobody()
    {
        Add("default", [WebhookEvents.All]);
        Add("crm", [WebhookEvents.EmailSent]);
        Add("identity", [WebhookEvents.EmailSent]);
        Add("crm", [WebhookEvents.EmailSent], active: false);

        var interested = await _repository.ListActiveForEventAsync("email.sent", "crm");

        Assert.Equal(["crm", "default"], interested.Select(s => s.ProjectCode).Order());
    }

    [Fact]
    public async Task APublishThatFails_NeverThrows_SoTheWorkThatCausedItStands()
    {
        var publisher = new OutboxEventPublisher(new ThrowingRepository(), _jobs, new FixedClock(Now), NullLogger<OutboxEventPublisher>.Instance);

        await publisher.PublishAsync(WebhookEvents.EmailSent, "crm", new { });

        Assert.Empty(_jobs.Jobs);
    }

    private sealed class ThrowingRepository : IWebhookRepository
    {
        public void Add(WebhookSubscription subscription) => throw new InvalidOperationException();
        public void Remove(WebhookSubscription subscription) => throw new InvalidOperationException();
        public Task<WebhookSubscription?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<WebhookSubscription>> ListAsync(string? projectCode, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<WebhookSubscription>> ListActiveForEventAsync(string eventName, string projectCode, CancellationToken cancellationToken = default) => throw new InvalidOperationException("database down");
        public void AddInbound(InboundWebhookLog log) => throw new InvalidOperationException();
        public Task<InboundWebhookLog?> GetInboundAsync(Guid id, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<InboundWebhookLog>> ListInboundAsync(string? source, int limit, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}

public class InboundReceiverTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private const string Secret = "telegram-secret-token-0123";

    private readonly FakeWebhookRepository _repository = new();
    private readonly FakeUsageRepository _usage = new();
    private readonly FakeInboundSecrets _secrets = new();
    private readonly FakeJobQueue _jobs = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private InboundWebhookReceiver Receiver() => new(_repository, _usage, _secrets, _jobs, _unitOfWork, new FixedClock(Now));

    private static Dictionary<string, string> Token(string token) => new(StringComparer.OrdinalIgnoreCase) { ["X-Telegram-Bot-Api-Secret-Token"] = token };

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"update_id":42}""");

    [Fact]
    public async Task ACallWithTheRightToken_IsStored_AndQueuedForProcessing()
    {
        _secrets.Secrets["telegram"] = Secret;

        var result = await Receiver().ReceiveAsync("Telegram", Token(Secret), Body);

        Assert.Equal(InboundOutcome.Accepted, result.Outcome);
        var log = Assert.Single(_repository.Inbound);
        Assert.Equal((result.LogId, "telegram", InboundWebhookStatus.Received, true), (log.Id, log.Source, log.Status, log.SignatureValid));
        Assert.Equal("""{"update_id":42}""", log.Payload);
        var job = Assert.Single(_jobs.Jobs);
        Assert.Equal((JobQueues.Webhook, "inbound"), (job.Queue, job.Type));
        Assert.Equal(log.Id, Assert.IsType<InboundWebhookPayload>(job.Payload).LogId);
    }

    [Fact]
    public async Task AWrongToken_IsRefused_LoggedWithoutTheBody_AndNothingIsQueued()
    {
        _secrets.Secrets["telegram"] = Secret;

        var result = await Receiver().ReceiveAsync("telegram", Token("guess"), Body);

        Assert.Equal(InboundOutcome.InvalidSignature, result.Outcome);
        var log = Assert.Single(_repository.Inbound);
        Assert.Equal(InboundWebhookStatus.Rejected, log.Status);
        Assert.Null(log.Payload);
        Assert.Empty(_jobs.Jobs);
    }

    [Fact]
    public async Task ASourceWithoutASecret_RefusesEverything_EvenACallThatCarriesAnEmptyToken()
    {
        var withoutHeader = await Receiver().ReceiveAsync("telegram", Token(string.Empty), Body);
        var withHeader = await Receiver().ReceiveAsync("telegram", Token("anything"), Body);

        Assert.Equal(InboundOutcome.NotConfigured, withoutHeader.Outcome);
        Assert.Equal(InboundOutcome.NotConfigured, withHeader.Outcome);
        Assert.Empty(_jobs.Jobs);
    }

    [Fact]
    public async Task AnUnknownSource_IsNotLogged_SoNobodyCanFillTheTableWithNames()
    {
        var result = await Receiver().ReceiveAsync("paypal", Token(Secret), Body);

        Assert.Equal(InboundOutcome.UnknownSource, result.Outcome);
        Assert.Empty(_repository.Inbound);
    }

    [Fact]
    public async Task ABodyOverTheLimit_IsRefused_BeforeItsSignatureIsLookedAt()
    {
        _secrets.Secrets["telegram"] = Secret;

        var result = await Receiver().ReceiveAsync("telegram", Token(Secret), new byte[InboundSources.MaxBodyBytes + 1]);

        Assert.Equal(InboundOutcome.TooLarge, result.Outcome);
        Assert.Equal(InboundWebhookStatus.Rejected, Assert.Single(_repository.Inbound).Status);
        Assert.Empty(_jobs.Jobs);
    }

    [Fact]
    public async Task TheSources_ListTheirAddressAndWhetherASecretIsConfigured_WithTheLast24Hours()
    {
        _secrets.Secrets["github"] = "x";
        _usage.Inbound = (source, status) => source == "github" ? (status == InboundWebhookStatus.Rejected ? 2 : 1) : 0;

        var sources = await Receiver().GetSourcesAsync();

        Assert.Equal(["telegram", "github", "stripe"], sources.Select(s => s.Source));
        var github = sources[1];
        Assert.Equal(("/api/v1/webhooks/incoming/github", true, 3, 2), (github.Path, github.Configured, github.Received24h, github.Rejected24h));
        Assert.False(sources[0].Configured);
    }
}

public class ScheduleRunnerTests
{
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 12, 10, 2, 0, TimeSpan.Zero));
    private readonly ScheduleState _state = new();

    private sealed class CountingTask(string id, string cron, Func<Task<string>>? run = null) : IScheduledTask
    {
        public int Runs { get; private set; }
        public string Id => id;
        public string Name => "Task " + id;
        public string Description => "Does something";
        public string Cron => cron;

        public async Task<string> RunAsync(CancellationToken cancellationToken)
        {
            Runs++;
            return run is null ? "done" : await run();
        }
    }

    private ScheduleRunner Runner(params CountingTask[] tasks)
    {
        var services = new ServiceCollection();
        foreach (var task in tasks) services.AddSingleton<IScheduledTask>(task);
        return new ScheduleRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _state, _clock, NullLogger<ScheduleRunner>.Instance);
    }

    [Fact]
    public async Task EveryTask_IsListedWithItsCronAndTheNextTimeItWillRun()
    {
        var runner = Runner(new CountingTask("a", "*/5 * * * *"), new CountingTask("b", "0 2 * * *"));

        var tasks = await runner.ListAsync();

        Assert.Equal(["a", "b"], tasks.Select(t => t.Id));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 10, 5, 0, TimeSpan.Zero), tasks[0].NextRunAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 2, 0, 0, TimeSpan.Zero), tasks[1].NextRunAt);
        Assert.All(tasks, t => Assert.Equal((null, 0, false), (t.LastStatus, t.Runs, t.IsRunning)));
    }

    [Fact]
    public async Task ATaskRunByHand_RecordsItsResult_ButKeepsItsSchedule()
    {
        var task = new CountingTask("a", "*/5 * * * *");
        var runner = Runner(task);
        var before = (await runner.ListAsync())[0].NextRunAt;

        var after = await runner.RunNowAsync("a");

        Assert.Equal(1, task.Runs);
        Assert.Equal(("Success: done", 1, _clock.GetUtcNow()), (after.LastStatus, after.Runs, after.LastRunAt));
        Assert.Equal(before, after.NextRunAt);
    }

    [Fact]
    public async Task AFailingTask_IsRecordedAsFailed_WithAShortError_AndTheNextRunStillComes()
    {
        var task = new CountingTask("a", "*/5 * * * *", () => throw new InvalidOperationException("disk\nfull"));
        var runner = Runner(task);

        var result = await runner.RunNowAsync("a");

        Assert.Equal(("Failed", "disk full", false), (result.LastStatus, result.LastError, result.IsRunning));
        Assert.Equal(1, result.Runs);
    }

    [Fact]
    public async Task AnUnknownTask_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Runner().RunNowAsync("nope"));
    }

    [Fact]
    public async Task OnlyTheTasksWhoseTimeHasCome_AreRun_AndTheyAreRescheduled()
    {
        var due = new CountingTask("due", "*/5 * * * *");
        var later = new CountingTask("later", "0 2 * * *");
        var runner = Runner(due, later);
        await runner.ListAsync();                       // sets the first times: 10:05 and tomorrow 02:00

        await runner.RunDueAsync(CancellationToken.None);
        Assert.Equal((0, 0), (due.Runs, later.Runs));   // nothing is due at 10:02

        _clock.Advance(TimeSpan.FromMinutes(4));        // 10:06
        await runner.RunDueAsync(CancellationToken.None);
        await runner.RunDueAsync(CancellationToken.None);   // a second pass in the same minute must not run it again

        Assert.Equal((1, 0), (due.Runs, later.Runs));
        var listed = (await runner.ListAsync()).Single(t => t.Id == "due");
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 10, 10, 0, TimeSpan.Zero), listed.NextRunAt);
    }

    [Fact]
    public async Task ATaskThatIsStillRunning_IsNotStartedASecondTime()
    {
        var gate = new TaskCompletionSource<string>();
        var task = new CountingTask("slow", "*/5 * * * *", () => gate.Task);
        var runner = Runner(task);

        var first = runner.RunNowAsync("slow");
        await Task.Delay(50);
        var second = await runner.RunNowAsync("slow");

        Assert.Equal(1, task.Runs);
        Assert.True(second.IsRunning);
        gate.SetResult("finished");
        var done = await first;
        Assert.Equal(("Success: finished", false), (done.LastStatus, done.IsRunning));
    }
}

public class ScheduledTaskTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheLogRetention_DeletesLogsOlderThanTheRetentionDays_AndNeverTheAuditLog()
    {
        var retention = new FakeRetentionRepository();

        var status = await new LogRetentionTask(retention, new JobSettings { LogRetentionDays = 30 }, new FixedClock(Now)).RunAsync(CancellationToken.None);

        var cutoff = Now.AddDays(-30);
        Assert.Equal(cutoff, retention.Cutoffs["delivery"]);
        Assert.Equal(["delivery", "media", "system", "notification"], retention.Cutoffs.Keys);
        Assert.DoesNotContain("audit", retention.Cutoffs.Keys);
        Assert.Contains("8", status);   // 4 tables, 2 rows each
    }

    [Fact]
    public async Task TheHousekeeping_UsesItsOwnPeriodsForFinishedJobsAndInboundCalls()
    {
        var retention = new FakeRetentionRepository();

        await new JobHousekeepingTask(retention, new JobSettings { FinishedJobRetentionDays = 3, LogRetentionDays = 40 }, new FixedClock(Now)).RunAsync(CancellationToken.None);

        Assert.Equal(Now.AddDays(-3), retention.Cutoffs["jobs"]);
        Assert.Equal(Now.AddDays(-40), retention.Cutoffs["inbound"]);
    }
}
