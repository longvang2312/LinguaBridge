using System.Text.Json;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.SharedServices.UnitTests;

/// <summary>Stands in for the quota guard: lets a test decide what it refuses and sees what it was asked.</summary>
internal sealed class FakeQuotaGuard : IQuotaGuard
{
    public Exception? Refuse { get; set; }
    public List<(string Project, ChannelType Channel)> SendChecks { get; } = [];
    public List<(string Project, long Bytes)> StoreChecks { get; } = [];

    public Task EnsureCanSendAsync(string projectCode, ChannelType channel, CancellationToken cancellationToken = default)
    {
        SendChecks.Add((projectCode, channel));
        return Refuse is null ? Task.CompletedTask : throw Refuse;
    }

    public Task EnsureCanStoreAsync(string projectCode, long incomingBytes, CancellationToken cancellationToken = default)
    {
        StoreChecks.Add((projectCode, incomingBytes));
        return Refuse is null ? Task.CompletedTask : throw Refuse;
    }
}

internal sealed class RecordingEvents : IEventPublisher
{
    public List<(string Event, string Project, JsonElement Data)> Published { get; } = [];

    public Task PublishAsync(string eventName, string projectCode, object data, CancellationToken cancellationToken = default)
    {
        Published.Add((eventName, projectCode, JsonSerializer.SerializeToElement(data)));
        return Task.CompletedTask;
    }
}

public class QuotaAndEventHooksTests
{
    private static QuotaExceededException Over(QuotaKind kind = QuotaKind.DailyEmail) => new(kind, 5, 5, DateTimeOffset.UtcNow.AddHours(1), "over");

    // ------------------------------------------------------------------------------------------------ email

    private sealed class EmailFixture
    {
        public ManagerFixture Configs { get; } = new();
        public FakeEmailProvider Mock { get; } = new(ProviderType.Mock);
        public FakeDeliveryLogRepository Logs { get; } = new();
        public FakeUnitOfWork Unit { get; } = new();
        public FakeQuotaGuard Quota { get; } = new();
        public RecordingEvents Events { get; } = new();
        public EmailDispatcher Dispatcher { get; }

        public EmailFixture()
        {
            Dispatcher = new EmailDispatcher(Configs.Manager, [Mock], new EmailTemplateRenderer(), Logs, Unit, Configs.Clock, NullLogger<EmailDispatcher>.Instance, Quota, Events);
        }

        public async Task ActivateAsync(string project = "crm")
        {
            var saved = await Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand(project, ChannelType.Email, ProviderType.Mock, "Email", """{"senderEmail":"noreply@example.com"}""", null));
            await Configs.Manager.ActivateAsync(saved.Id);
        }
    }

    private static SendEmailCommand Mail(string to = "lan@example.com") => new("crm", to, Subject: "Hello", TextBody: "Hi");

    [Fact]
    public async Task AnEmail_AsksTheQuotaGuardFirst_AndAProjectOverItsQuotaSendsNothingAndLogsNothing()
    {
        var f = new EmailFixture();
        await f.ActivateAsync();
        f.Quota.Refuse = Over();

        await Assert.ThrowsAsync<QuotaExceededException>(() => f.Dispatcher.SendAsync(Mail()));

        Assert.Equal([("crm", ChannelType.Email)], f.Quota.SendChecks);
        Assert.Empty(f.Mock.Sent);
        Assert.Empty(f.Logs.Rows);
        Assert.Empty(f.Events.Published);
    }

    [Fact]
    public async Task ABadRequest_IsRefusedAsBadBeforeTheQuotaIsConsulted()
    {
        var f = new EmailFixture();
        f.Quota.Refuse = Over();

        await Assert.ThrowsAsync<ValidationException>(() => f.Dispatcher.SendAsync(Mail("not-an-address")));

        Assert.Empty(f.Quota.SendChecks);
    }

    [Fact]
    public async Task ADeliveredEmail_PublishesEmailSent_WithTheRecipientAndTheLogId_ButNotTheContent()
    {
        var f = new EmailFixture();
        await f.ActivateAsync();

        var result = await f.Dispatcher.SendAsync(Mail());

        var published = Assert.Single(f.Events.Published);
        Assert.Equal(("email.sent", "crm"), (published.Event, published.Project));
        Assert.Equal("lan@example.com", published.Data.GetProperty("recipient").GetString());
        Assert.Equal(result.DeliveryLogId, published.Data.GetProperty("deliveryLogId").GetGuid());
        Assert.DoesNotContain("Hello", published.Data.GetRawText());
        Assert.DoesNotContain("Hi", published.Data.GetProperty("recipient").GetString()!.Replace("lan@example.com", ""));
    }

    [Fact]
    public async Task AFailedEmail_PublishesEmailFailed_WithTheReason()
    {
        var f = new EmailFixture();
        await f.ActivateAsync();
        f.Mock.SendResult = new EmailSendResult(false, Error: "mailbox full");

        await f.Dispatcher.SendAsync(Mail());

        var published = Assert.Single(f.Events.Published);
        Assert.Equal("email.failed", published.Event);
        Assert.Equal("mailbox full", published.Data.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithNoConfiguration_TheFailureIsPublishedToo()
    {
        var f = new EmailFixture();

        var result = await f.Dispatcher.SendAsync(Mail());

        Assert.Equal(EmailFailure.NoConfiguration, result.Failure);
        Assert.Equal("email.failed", Assert.Single(f.Events.Published).Event);
    }

    [Fact]
    public async Task TheHooks_AreOptional_SoAnEmailDispatcherWithoutThemStillWorks()
    {
        var configs = new ManagerFixture();
        var dispatcher = new EmailDispatcher(configs.Manager, [new FakeEmailProvider(ProviderType.Mock)], new EmailTemplateRenderer(), new FakeDeliveryLogRepository(), new FakeUnitOfWork(), configs.Clock, NullLogger<EmailDispatcher>.Instance);
        var saved = await configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("crm", ChannelType.Email, ProviderType.Mock, "Email", """{"senderEmail":"noreply@example.com"}""", null));
        await configs.Manager.ActivateAsync(saved.Id);

        Assert.True((await dispatcher.SendAsync(Mail())).Success);
    }

    // ------------------------------------------------------------------------------------------------ chat

    [Fact]
    public async Task AChatMessage_AsksTheGuardForTheChatQuota_AndPublishesChatSentOrChatFailed()
    {
        var configs = new ManagerFixture();
        var provider = new FakeChatProvider(ProviderType.Mock);
        var quota = new FakeQuotaGuard();
        var events = new RecordingEvents();
        var dispatcher = new NotificationBotDispatcher(configs.Manager, [provider], new FakeDeliveryLogRepository(), new FakeUnitOfWork(), configs.Clock, NullLogger<NotificationBotDispatcher>.Instance, quota, events);
        var saved = await configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("crm", ChannelType.Chat, ProviderType.Mock, "Chat", "{}", null));
        await configs.Manager.ActivateAsync(saved.Id);

        await dispatcher.SendAsync(new SendBotMessageCommand("crm", "hello"));
        provider.SendResult = new BotSendResult(false, Error: "bot blocked");
        await dispatcher.SendAsync(new SendBotMessageCommand("crm", "again"));
        quota.Refuse = Over(QuotaKind.DailyChat);
        await Assert.ThrowsAsync<QuotaExceededException>(() => dispatcher.SendAsync(new SendBotMessageCommand("crm", "third")));

        Assert.Equal([("crm", ChannelType.Chat), ("crm", ChannelType.Chat), ("crm", ChannelType.Chat)], quota.SendChecks);
        Assert.Equal(["chat.sent", "chat.failed"], events.Published.Select(p => p.Event));
        Assert.Equal(2, provider.Sent.Count);   // the third never reached the provider
        Assert.DoesNotContain("hello", events.Published[0].Data.GetRawText());   // the message itself is not published
    }

    // ------------------------------------------------------------------------------------------------ storage

    private sealed class StorageFixture
    {
        public ManagerFixture Configs { get; } = new();
        public FakeStorageProvider Disk { get; } = new(ProviderType.LocalDisk);
        public FakeStoredFileRepository Files { get; } = new();
        public FakeMediaLogRepository Logs { get; } = new();
        public FakeQuotaGuard Quota { get; } = new();
        public RecordingEvents Events { get; } = new();
        public StorageService Service { get; }

        public StorageFixture()
        {
            Service = new StorageService(Configs.Manager, [Disk], new FakeThumbnails(), Files, Logs, new FakeUnitOfWork(), Configs.Clock, NullLogger<StorageService>.Instance, Quota, Events);
        }

        public async Task ActivateAsync()
        {
            var saved = await Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("crm", ChannelType.Storage, ProviderType.LocalDisk, "Storage", """{"rootPath":"uploads","maxFileSizeMb":1}""", null));
            await Configs.Manager.ActivateAsync(saved.Id);
        }

        public Task<UploadFileResult> UploadAsync(byte[] bytes) => Service.UploadAsync(new UploadFileCommand("crm", "a.png", "image/png", new MemoryStream(bytes)));
    }

    [Fact]
    public async Task AnUpload_AsksOnceBeforeReadingAnything_AndOnceAgainWithTheRealSize()
    {
        var f = new StorageFixture();
        await f.ActivateAsync();

        await f.UploadAsync(TestFiles.FakePng(500));

        Assert.Equal([("crm", 0L), ("crm", 500L)], f.Quota.StoreChecks);
    }

    [Fact]
    public async Task WhenThereIsNoRoomAtAll_NothingIsReadOrStored()
    {
        var f = new StorageFixture();
        await f.ActivateAsync();
        f.Quota.Refuse = Over(QuotaKind.Storage);

        var stream = new TrackingStream(TestFiles.FakePng(500));
        await Assert.ThrowsAsync<QuotaExceededException>(() => f.Service.UploadAsync(new UploadFileCommand("crm", "a.png", "image/png", stream)));

        Assert.Equal(0, stream.BytesRead);
        Assert.Empty(f.Disk.Objects);
        Assert.Empty(f.Files.Rows);
        Assert.Equal([("crm", 0L)], f.Quota.StoreChecks);
    }

    [Fact]
    public async Task AFileThatWouldGoOverTheQuota_IsRefusedAfterItIsReadButBeforeItIsWritten()
    {
        var f = new StorageFixture();
        await f.ActivateAsync();
        f.Quota.Refuse = null;
        var guard = new SizeAwareGuard(maxIncoming: 100);
        var service = new StorageService(f.Configs.Manager, [f.Disk], new FakeThumbnails(), f.Files, f.Logs, new FakeUnitOfWork(), f.Configs.Clock, NullLogger<StorageService>.Instance, guard, f.Events);

        await Assert.ThrowsAsync<QuotaExceededException>(() => service.UploadAsync(new UploadFileCommand("crm", "a.png", "image/png", new MemoryStream(TestFiles.FakePng(500)))));

        Assert.Empty(f.Disk.Objects);
        Assert.Empty(f.Files.Rows);
        Assert.Empty(f.Events.Published);
    }

    [Fact]
    public async Task AStoredFile_PublishesFileUploaded_WithItsIdNameTypeAndSize()
    {
        var f = new StorageFixture();
        await f.ActivateAsync();

        var result = await f.UploadAsync(TestFiles.FakePng(500));

        var published = Assert.Single(f.Events.Published);
        Assert.Equal(("file.uploaded", "crm"), (published.Event, published.Project));
        Assert.Equal(result.File!.Id, published.Data.GetProperty("fileId").GetGuid());
        Assert.Equal(("a.png", "image/png", 500), (published.Data.GetProperty("fileName").GetString(), published.Data.GetProperty("mimeType").GetString(), published.Data.GetProperty("sizeBytes").GetInt32()));
    }

    [Fact]
    public async Task AnUploadThatFailsToBeWritten_PublishesNothing()
    {
        var f = new StorageFixture();
        await f.ActivateAsync();
        f.Disk.PutFailure = new IOException("disk full");

        var result = await f.UploadAsync(TestFiles.FakePng(500));

        Assert.False(result.Success);
        Assert.Empty(f.Events.Published);
    }

    private sealed class SizeAwareGuard(long maxIncoming) : IQuotaGuard
    {
        public Task EnsureCanSendAsync(string projectCode, ChannelType channel, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureCanStoreAsync(string projectCode, long incomingBytes, CancellationToken cancellationToken = default) =>
            incomingBytes > maxIncoming ? throw new QuotaExceededException(QuotaKind.Storage, maxIncoming, 0, null, "no room") : Task.CompletedTask;
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }
}

public class JobHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static BackgroundJobRecord Job(string queue, string type, object payload) =>
        BackgroundJobRecord.Create(queue, type, JobJson.Serialize(payload), 4, Now);

    // ------------------------------------------------------------------------------------------------ email and chat

    private sealed class FixedEmailDispatcher(Func<SendEmailResult> result) : IEmailDispatcher
    {
        public List<SendEmailCommand> Sent { get; } = [];

        public Task<SendEmailResult> SendAsync(SendEmailCommand command, CancellationToken cancellationToken = default)
        {
            Sent.Add(command);
            return Task.FromResult(result());
        }

        public Task<IReadOnlyList<DeliveryLogDto>> RecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingEmailDispatcher(Exception failure) : IEmailDispatcher
    {
        public Task<SendEmailResult> SendAsync(SendEmailCommand command, CancellationToken cancellationToken = default) => throw failure;

        public Task<IReadOnlyList<DeliveryLogDto>> RecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static SendEmailResult Sent() => new(true, EmailFailure.None, Guid.NewGuid(), 5, null);

    [Fact]
    public async Task AnEmailJob_SendsTheStoredMessage_AndCompletesWhenItWentOut()
    {
        var dispatcher = new FixedEmailDispatcher(Sent);

        await new EmailJobHandler(dispatcher).HandleAsync(Job(JobQueues.Email, "send", new EmailJobPayload("crm", "lan@example.com", "Hello", "<p>Hi</p>", "Hi")), CancellationToken.None);

        var command = Assert.Single(dispatcher.Sent);
        Assert.Equal(("crm", "lan@example.com", "Hello", "<p>Hi</p>", "Hi"), (command.ProjectCode, command.To, command.Subject, command.HtmlBody, command.TextBody));
    }

    [Fact]
    public async Task AProviderFailure_MakesTheEmailJobFailSoItIsTriedAgain()
    {
        var dispatcher = new FixedEmailDispatcher(() => new SendEmailResult(false, EmailFailure.ProviderFailure, Guid.NewGuid(), 5, "smtp refused"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EmailJobHandler(dispatcher).HandleAsync(Job(JobQueues.Email, "send", new EmailJobPayload("crm", "a@b.co", "S", "h", "t")), CancellationToken.None));

        Assert.Equal("smtp refused", error.Message);
    }

    [Fact]
    public async Task NoConfiguration_ADeactivatedProject_AnExhaustedQuota_AndABadMessage_FailForGood()
    {
        var job = Job(JobQueues.Email, "send", new EmailJobPayload("crm", "a@b.co", "S", "h", "t"));

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => new EmailJobHandler(
            new FixedEmailDispatcher(() => new SendEmailResult(false, EmailFailure.NoConfiguration, null, 0, "no config"))).HandleAsync(job, CancellationToken.None));
        await Assert.ThrowsAsync<JobPermanentFailureException>(() => new EmailJobHandler(
            new ThrowingEmailDispatcher(new ProjectInactiveException("crm"))).HandleAsync(job, CancellationToken.None));
        await Assert.ThrowsAsync<JobPermanentFailureException>(() => new EmailJobHandler(
            new ThrowingEmailDispatcher(new QuotaExceededException(QuotaKind.DailyEmail, 1, 1, null, "quota"))).HandleAsync(job, CancellationToken.None));
        await Assert.ThrowsAsync<JobPermanentFailureException>(() => new EmailJobHandler(
            new ThrowingEmailDispatcher(new ValidationException(new Dictionary<string, string[]> { ["To"] = ["bad"] }))).HandleAsync(job, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnexpectedException_IsNotMadePermanent_SoTheJobIsTriedAgain()
    {
        var job = Job(JobQueues.Email, "send", new EmailJobPayload("crm", "a@b.co", "S", "h", "t"));

        await Assert.ThrowsAsync<TimeoutException>(() => new EmailJobHandler(new ThrowingEmailDispatcher(new TimeoutException())).HandleAsync(job, CancellationToken.None));
    }

    [Fact]
    public async Task AJobWhosePayloadCannotBeRead_FailsForGood_BecauseItNeverWillBeReadable()
    {
        var job = BackgroundJobRecord.Create(JobQueues.Email, "send", "this is not json", 4, Now);

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => new EmailJobHandler(new FixedEmailDispatcher(Sent)).HandleAsync(job, CancellationToken.None));
    }

    [Fact]
    public async Task AChatJob_SendsAsPlainText_ToTheStoredRecipient_AndRetriesAProviderFailure()
    {
        var provider = new FakeChatProvider(ProviderType.Mock);
        var configs = new ManagerFixture();
        var saved = await configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("crm", ChannelType.Chat, ProviderType.Mock, "Chat", "{}", null));
        await configs.Manager.ActivateAsync(saved.Id);
        var dispatcher = new NotificationBotDispatcher(configs.Manager, [provider], new FakeDeliveryLogRepository(), new FakeUnitOfWork(), configs.Clock, NullLogger<NotificationBotDispatcher>.Instance);
        var handler = new ChatJobHandler(dispatcher);

        await handler.HandleAsync(Job(JobQueues.Chat, "send", new ChatJobPayload("crm", "42", "*hello*")), CancellationToken.None);
        provider.SendResult = new BotSendResult(false, Error: "bot blocked");
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Job(JobQueues.Chat, "send", new ChatJobPayload("crm", "42", "again")), CancellationToken.None));

        var (message, _) = provider.Sent[0];
        Assert.Equal(("42", "*hello*", BotFormat.PlainText), (message.Recipient, message.Text, message.Format));
    }

    // ------------------------------------------------------------------------------------------------ outbox and webhooks

    private static WebhookSubscription Subscription(string project, params string[] events) =>
        WebhookSubscription.Create(project, "App", "https://app.example.com/hook", new FakeSecretProtector().Protect("whsec_0123456789abcdef"), "whsec_0123456789abcdef", events, Now);

    [Fact]
    public async Task TheOutboxJob_MakesOneWebhookJobPerSubscriber_EachCarryingTheSameBodyToBeSigned()
    {
        var repository = new FakeWebhookRepository();
        var a = Subscription("crm", WebhookEvents.EmailSent);
        var b = Subscription("default", WebhookEvents.All);
        var other = Subscription("crm", WebhookEvents.ChatSent);
        repository.Rows.AddRange([a, b, other]);
        var jobs = new FakeJobQueue();

        await new OutboxJobHandler(repository, jobs).HandleAsync(
            Job(JobQueues.Outbox, "event", new OutboxEventPayload("evt-1", "email.sent", "crm", """{"id":"evt-1"}""")), CancellationToken.None);

        Assert.Equal(2, jobs.Jobs.Count);
        Assert.All(jobs.Jobs, j => Assert.Equal((JobQueues.Webhook, "deliver"), (j.Queue, j.Type)));
        var payloads = jobs.Jobs.Select(j => Assert.IsType<WebhookDeliveryPayload>(j.Payload)).ToList();
        Assert.Equivalent(new[] { a.Id, b.Id }, payloads.Select(p => p.SubscriptionId));
        Assert.All(payloads, p => Assert.Equal(("evt-1", "email.sent", """{"id":"evt-1"}"""), (p.EventId, p.Event, p.Body)));
    }

    private (WebhookJobHandler Handler, FakeWebhookRepository Repository, FakeWebhookSender Sender, FakeUnitOfWork Unit, RecordingEvents Events) Webhooks()
    {
        var repository = new FakeWebhookRepository();
        var sender = new FakeWebhookSender();
        var unit = new FakeUnitOfWork();
        var events = new RecordingEvents();
        var clock = new FixedClock(Now);
        var deliverer = new WebhookDeliverer(sender, new FakeSecretProtector(), clock, NullLogger<WebhookDeliverer>.Instance);
        return (new WebhookJobHandler(repository, deliverer, events, unit, clock), repository, sender, unit, events);
    }

    private static BackgroundJobRecord Delivery(WebhookSubscription subscription) =>
        Job(JobQueues.Webhook, "deliver", new WebhookDeliveryPayload(subscription.Id, "evt-1", "email.sent", """{"id":"evt-1"}"""));

    [Fact]
    public async Task ADeliveryJob_PostsTheSignedBody_AndSavesTheCounters()
    {
        var (handler, repository, sender, unit, _) = Webhooks();
        var subscription = Subscription("crm", WebhookEvents.EmailSent);
        repository.Rows.Add(subscription);

        await handler.HandleAsync(Delivery(subscription), CancellationToken.None);

        var post = Assert.Single(sender.Posts);
        Assert.Equal("""{"id":"evt-1"}""", post.Body);
        Assert.Equal((1, 0), (subscription.SuccessCount, subscription.FailureCount));
        Assert.Equal(1, unit.Saves);
    }

    [Fact]
    public async Task ADeliveryThatFailsOnTheirSide_ThrowsToBeRetried_AFourHundredGivesUp()
    {
        var (handler, repository, sender, _, _) = Webhooks();
        var subscription = Subscription("crm", WebhookEvents.EmailSent);
        repository.Rows.Add(subscription);

        sender.Result = () => new WebhookHttpResult(503, null);
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Delivery(subscription), CancellationToken.None));
        sender.Result = () => new WebhookHttpResult(400, null);
        await Assert.ThrowsAsync<JobPermanentFailureException>(() => handler.HandleAsync(Delivery(subscription), CancellationToken.None));

        Assert.Contains("503", retry.Message);
        Assert.Equal(2, subscription.FailureCount);
    }

    [Fact]
    public async Task ADeliveryForASubscriptionThatIsGoneOrSwitchedOff_IsDroppedQuietly()
    {
        var (handler, repository, sender, _, _) = Webhooks();
        var off = Subscription("crm", WebhookEvents.EmailSent);
        off.SetActive(false);
        repository.Rows.Add(off);

        await handler.HandleAsync(Delivery(off), CancellationToken.None);
        await handler.HandleAsync(Job(JobQueues.Webhook, "deliver", new WebhookDeliveryPayload(Guid.NewGuid(), "e", "email.sent", "{}")), CancellationToken.None);

        Assert.Empty(sender.Posts);
    }

    [Fact]
    public async Task AnInboundJob_HandsTheCallOnAsAWebhookReceivedEvent_AndMarksItProcessed()
    {
        var (handler, repository, _, unit, events) = Webhooks();
        var log = InboundWebhookLog.Accepted("github", """{"ref":"main"}""", 14, Now);
        repository.Inbound.Add(log);

        await handler.HandleAsync(Job(JobQueues.Webhook, "inbound", new InboundWebhookPayload(log.Id)), CancellationToken.None);

        var published = Assert.Single(events.Published);
        Assert.Equal(("webhook.received", "default"), (published.Event, published.Project));
        Assert.Equal(("github", """{"ref":"main"}"""), (published.Data.GetProperty("source").GetString(), published.Data.GetProperty("payload").GetString()));
        Assert.Equal(InboundWebhookStatus.Processed, log.Status);
        Assert.Equal(1, unit.Saves);
    }

    [Fact]
    public async Task AnInboundJobWhoseCallIsGone_FailsForGood_AndAnUnknownJobTypeToo()
    {
        var (handler, _, _, _, _) = Webhooks();

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => handler.HandleAsync(Job(JobQueues.Webhook, "inbound", new InboundWebhookPayload(Guid.NewGuid())), CancellationToken.None));
        await Assert.ThrowsAsync<JobPermanentFailureException>(() => handler.HandleAsync(Job(JobQueues.Webhook, "mystery", new { }), CancellationToken.None));
    }

    // ------------------------------------------------------------------------------------------------ thumbnails

    private sealed class ThumbnailFixture
    {
        public ManagerFixture Configs { get; } = new();
        public FakeStorageProvider Disk { get; } = new(ProviderType.LocalDisk);
        public FakeThumbnails Thumbs { get; } = new();
        public FakeStoredFileRepository Files { get; } = new();
        public FakeMediaLogRepository Logs { get; } = new();
        public ThumbnailRebuilder Rebuilder { get; }

        public ThumbnailFixture()
        {
            Rebuilder = new ThumbnailRebuilder(Configs.Manager, [Disk], Thumbs, Files, Logs, new FakeUnitOfWork(), Configs.Clock, NullLogger<ThumbnailRebuilder>.Instance);
        }

        public async Task<StoredFile> AddFileAsync(string mime = "image/png", bool storeOriginal = true)
        {
            var saved = await Configs.Manager.SaveAsync(new SaveChannelConfigurationCommand("crm", ChannelType.Storage, ProviderType.LocalDisk, "Storage", """{"rootPath":"uploads"}""", null));
            var file = StoredFile.Create("crm", saved.Id, ProviderType.LocalDisk, "a.png", mime, 200, new string('a', 64), false, Now);
            Files.Rows.Add(file);
            if (storeOriginal) Disk.Objects[file.ObjectKey] = TestFiles.FakePng();
            return file;
        }
    }

    [Fact]
    public async Task ARebuild_ReadsTheOriginalFromTheStorageItWasWrittenTo_AndWritesTheThumbnailAgain()
    {
        var f = new ThumbnailFixture();
        var file = await f.AddFileAsync();

        var result = await f.Rebuilder.RebuildAsync(file.Id);

        Assert.Equal(ThumbnailRebuildOutcome.Rebuilt, result.Outcome);
        Assert.Equal(f.Thumbs.Result, f.Disk.Objects[file.ThumbnailObjectKey]);
        Assert.Equal(("image/webp", file.ThumbnailObjectKey), (f.Disk.Puts.Single().Mime, f.Disk.Puts.Single().Key));
        Assert.Equal(("Thumbnail", "Success"), (f.Logs.Rows.Single().Action, f.Logs.Rows.Single().Status));
    }

    [Fact]
    public async Task ARebuildOfAMissingFileOrANonImage_IsAPermanentFailureOfTheJob()
    {
        var f = new ThumbnailFixture();
        var pdf = await f.AddFileAsync("application/pdf");
        var handler = new MediaThumbnailJobHandler(f.Rebuilder);

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => handler.HandleAsync(Job(JobQueues.MediaThumbnail, "rebuild", new ThumbnailJobPayload(Guid.NewGuid())), CancellationToken.None));
        await Assert.ThrowsAsync<JobPermanentFailureException>(() => handler.HandleAsync(Job(JobQueues.MediaThumbnail, "rebuild", new ThumbnailJobPayload(pdf.Id)), CancellationToken.None));
    }

    [Fact]
    public async Task AnOriginalThatIsNoLongerInTheStorage_OrAnUnreadableOne_IsRetriedAndLogged()
    {
        var f = new ThumbnailFixture();
        var missing = await f.AddFileAsync(storeOriginal: false);
        var handler = new MediaThumbnailJobHandler(f.Rebuilder);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Job(JobQueues.MediaThumbnail, "rebuild", new ThumbnailJobPayload(missing.Id)), CancellationToken.None));

        var present = await f.AddFileAsync();
        f.Disk.ReadFailure = new IOException("disk error");
        var failed = await f.Rebuilder.RebuildAsync(present.Id);

        Assert.Equal(ThumbnailRebuildOutcome.Failed, failed.Outcome);
        Assert.Equal(2, f.Logs.Rows.Count(l => l.Status == "Failed"));
    }

    [Fact]
    public async Task AnImageTheThumbnailServiceRefuses_IsReportedAsAFailureWithItsReason()
    {
        var f = new ThumbnailFixture();
        var file = await f.AddFileAsync();
        f.Thumbs.Failure = new ImageRejectedException("The image is too large in pixels.");

        var result = await f.Rebuilder.RebuildAsync(file.Id);

        Assert.Equal((ThumbnailRebuildOutcome.Failed, "The image is too large in pixels."), (result.Outcome, result.Error));
        Assert.DoesNotContain(file.ThumbnailObjectKey, f.Disk.Objects.Keys);
    }
}

public class MetricsCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeUsageRepository _usage = new();
    private readonly FakeBackgroundJobRepository _jobs = new();
    private readonly FakeWebhookRepository _webhooks = new();
    private readonly FixedClock _clock = new(Now);

    private MetricsCollector Collector(JobSettings? settings = null) =>
        new(_usage, _jobs, _webhooks, new MetricsSnapshotStore(_clock), settings ?? new JobSettings { MetricsCacheSeconds = 0 }, _clock);

    private static string StatusOf(SystemMetricsOverviewDto overview, string id) => overview.Services.Single(s => s.Id == id).Status;

    [Fact]
    public async Task ThereAreAlwaysEightServices_InTheOrderOfTheDesign()
    {
        var overview = await Collector().GetOverviewAsync();

        Assert.Equal(8, overview.ServicesTotal);
        Assert.Equal(["email", "chat", "storage", "projects", "notifications", "jobs", "webhooks", "monitoring"], overview.Services.Select(s => s.Id));
        Assert.Equal(overview.ServicesTotal, overview.ServicesHealthy + overview.ServicesDegraded + overview.ServicesIdle);
    }

    [Fact]
    public async Task AChannelWithNoActiveConfiguration_IsIdle_NotHealthy()
    {
        _usage.ActiveConfigurations = 0;

        var overview = await Collector().GetOverviewAsync();

        Assert.Equal(["Idle", "Idle", "Idle"], new[] { StatusOf(overview, "email"), StatusOf(overview, "chat"), StatusOf(overview, "storage") });
        Assert.Equal("No active email configuration.", overview.Services[0].Detail);
    }

    [Theory]
    [InlineData(9, 1, "Healthy")]     // 10 deliveries, 10% failed
    [InlineData(8, 2, "Healthy")]     // exactly a fifth failed: not more than a fifth
    [InlineData(7, 3, "Degraded")]    // 30% failed
    [InlineData(0, 5, "Degraded")]    // everything failed, but enough of it to judge
    [InlineData(0, 4, "Healthy")]     // too few to judge: one bad hour does not make a service degraded
    [InlineData(0, 0, "Healthy")]
    public async Task AChannelIsDegraded_WhenMoreThanAFifthOfAtLeastFiveDeliveriesFailed(int sent, int failed, string expected)
    {
        _usage.Deliveries = (channels, status) => channels.Contains(ChannelType.Email) ? (status == DeliveryStatus.Sent ? sent : failed) : 0;

        var overview = await Collector().GetOverviewAsync();

        Assert.Equal(expected, StatusOf(overview, "email"));
        Assert.Equal("Healthy", StatusOf(overview, "chat"));   // the other channel is judged on its own figures
    }

    [Fact]
    public async Task TheSuccessRate_IsTheShareOfDeliveriesThatWentOut_AndNullWhenThereWereNone()
    {
        Assert.Null((await Collector().GetOverviewAsync()).SuccessRatePercent);

        _usage.Deliveries = (channels, status) => channels.Contains(ChannelType.Email) ? (status == DeliveryStatus.Sent ? 7 : 1) : (status == DeliveryStatus.Sent ? 2 : 0);
        var overview = await Collector().GetOverviewAsync();

        Assert.Equal(90.0, overview.SuccessRatePercent);   // 9 of 10
        Assert.Equal((9, 1), (overview.DeliveriesSent24h, overview.DeliveriesFailed24h));
    }

    [Fact]
    public async Task TheJobService_IsDegradedWhileAnyJobHasFailed_AndShowsWhatWaits()
    {
        _jobs.Counts.Add(new QueueCounts("email", 3, 0, 10, 0));
        Assert.Equal("Healthy", StatusOf(await Collector().GetOverviewAsync(), "jobs"));

        _jobs.Counts.Add(new QueueCounts("webhook", 1, 0, 2, 2));
        var overview = await Collector().GetOverviewAsync();

        Assert.Equal("Degraded", StatusOf(overview, "jobs"));
        Assert.Equal((4, 2), (overview.PendingJobs, overview.FailedJobs));
        Assert.Equal("2", overview.Services.Single(s => s.Id == "jobs").Metrics["failed"]);
    }

    [Fact]
    public async Task TheWebhookService_IsIdleWithNoSubscriptionAndNoTraffic_ThenJudgedOnItsOutboundDeliveries()
    {
        Assert.Equal("Idle", StatusOf(await Collector().GetOverviewAsync(), "webhooks"));

        var subscription = WebhookSubscription.Create("crm", "App", "https://a.example.com", "p", new string('s', 16), [WebhookEvents.All], Now);
        _webhooks.Rows.Add(subscription);
        Assert.Equal("Healthy", StatusOf(await Collector().GetOverviewAsync(), "webhooks"));

        for (var i = 0; i < 3; i++) subscription.RecordDelivery(true, "200", Now);
        for (var i = 0; i < 3; i++) subscription.RecordDelivery(false, "500", Now);
        Assert.Equal("Degraded", StatusOf(await Collector().GetOverviewAsync(), "webhooks"));
    }

    [Fact]
    public async Task InboundTraffic_AloneIsEnoughToMakeTheWebhookServiceActive()
    {
        _usage.Inbound = (source, status) => source == "telegram" ? (status == InboundWebhookStatus.Rejected ? 0 : 3) : 0;

        var overview = await Collector().GetOverviewAsync();

        Assert.Equal("Healthy", StatusOf(overview, "webhooks"));
        Assert.Equal("3", overview.Services.Single(s => s.Id == "webhooks").Metrics["inboundAccepted24h"]);
    }

    [Fact]
    public async Task TotalRequests_AreTheDeliveriesFileOperationsInboundCallsAndQueuedJobs()
    {
        _usage.Deliveries = (channels, status) => channels.Contains(ChannelType.Email) && status == DeliveryStatus.Sent ? 4 : 0;
        _usage.MediaAll = 6;
        _usage.JobsCreated = 5;
        // Stripe received 3 calls in all (no status given means every row), one of them refused.
        _usage.Inbound = (source, status) => source == "stripe" ? (status == InboundWebhookStatus.Rejected ? 1 : 3) : 0;

        var overview = await Collector().GetOverviewAsync();

        Assert.Equal(4 + 6 + 5 + 3, overview.TotalRequests24h);
    }

    [Fact]
    public async Task TheNotificationService_ReportsItsTemplatesAndItsFailures()
    {
        _usage.NotificationsAll = 20;
        _usage.NotificationsFailed = 10;

        var overview = await Collector().GetOverviewAsync();

        Assert.Equal("Degraded", StatusOf(overview, "notifications"));
        Assert.Equal("10", overview.Services.Single(s => s.Id == "notifications").Metrics["failed24h"]);
    }

    [Fact]
    public async Task TheUptime_IsTheTimeSinceTheStoreWasCreated()
    {
        var collector = Collector();
        _clock.Advance(TimeSpan.FromMinutes(90));

        var overview = await collector.GetOverviewAsync();

        Assert.Equal(5400, overview.UptimeSeconds);
        Assert.Equal(Now, overview.StartedAt);
    }

    [Fact]
    public async Task ASnapshot_IsReusedWithinTheCacheTime_ButARefreshAlwaysRecomputes()
    {
        var settings = new JobSettings { MetricsCacheSeconds = 60 };
        var collector = Collector(settings);
        _usage.ActiveProjects = 3;
        var first = await collector.GetOverviewAsync();

        _usage.ActiveProjects = 5;
        var cached = await collector.GetOverviewAsync();
        var refreshed = await collector.GetOverviewAsync(refresh: true);
        _clock.Advance(TimeSpan.FromSeconds(61));
        var expired = await collector.GetOverviewAsync();

        Assert.Equal(3, first.ActiveProjects);
        Assert.Equal(3, cached.ActiveProjects);
        Assert.Equal(5, refreshed.ActiveProjects);
        Assert.Equal(5, expired.ActiveProjects);
    }

    [Fact]
    public async Task TheOverallHealth_IsDegradedIfAnyServiceIs_IdleIfNothingIsHealthy_ElseHealthy()
    {
        Assert.Equal("Healthy", (await Collector().GetHealthAsync()).Overall);

        _jobs.Counts.Add(new QueueCounts("email", 0, 0, 0, 1));
        Assert.Equal("Degraded", (await Collector().GetHealthAsync()).Overall);
    }
}
