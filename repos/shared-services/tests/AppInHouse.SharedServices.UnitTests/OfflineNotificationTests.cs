using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Notifications;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Realtime;
using AppInHouse.SharedServices.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppInHouse.SharedServices.UnitTests;

public class OfflineNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeConversationRepository _repo = new();
    private readonly UserPresenceTracker _presence = new();
    private readonly FakeNotificationDispatcher _dispatcher = new();
    private readonly Conversation _room;
    private int _tick;

    public OfflineNotificationTests()
    {
        // lan writes to minh, who left an email and a chat id.
        _room = Conversation.CreateDirect("crm", "lan", "minh", Now);
        _room.FindMember("minh")!.SetProfile("Minh", "minh@example.com", "chat-9");
        _room.FindMember("lan")!.SetProfile("Lan", "lan@example.com", null);
        _repo.Conversations.Add(_room);
    }

    private OfflineNotificationHandler Handler() => new(_repo, _presence, _dispatcher, NullLogger<OfflineNotificationHandler>.Instance);

    private ChatMessage Say(string from, string text, Guid? file = null)
    {
        var message = ChatMessage.Create(_room.Id, from, from == "lan" ? "Lan" : "Minh", text, file, Now.AddSeconds(++_tick));
        _repo.Messages.Add(message);
        _room.TouchLastMessage(message.CreatedAt);
        return message;
    }

    private static OfflineNotificationPayload For(Conversation room, ChatMessage message, string recipient = "minh", bool email = true, bool bot = true) =>
        new(room.ProjectCode, room.Id, message.Id, recipient, email, bot);

    // ------------------------------------------------------------------------------------------------ it speaks

    [Theory]
    [InlineData(true, false, 1, PlatformDefaults.ChatOfflineEmailTemplate)]
    [InlineData(false, true, 1, PlatformDefaults.ChatOfflineChatTemplate)]
    [InlineData(false, false, 0, null)]
    public async Task TheNotice_GoesOutOnlyByTheWaysTheSenderMaySend(bool email, bool bot, int expected, string? template)
    {
        var message = Say("lan", "ping");

        await Handler().HandleAsync(For(_room, message, email: email, bot: bot));

        Assert.Equal(expected, _dispatcher.Commands.Count);
        if (template is not null) Assert.Equal(template, _dispatcher.Commands.Single().TemplateCode);
    }

    [Fact]
    public async Task AJobQueuedWithoutThePermissions_SendsNothing()
    {
        var message = Say("lan", "ping");

        await Handler().HandleAsync(new OfflineNotificationPayload(_room.ProjectCode, _room.Id, message.Id, "minh"));

        Assert.Empty(_dispatcher.Commands);
    }

    [Fact]
    public async Task AnOfflineMember_GetsTheNotice_ByEmailAndByChat_WithTheSenderAndAShortPreview()
    {
        var message = Say("lan", "Hẹn gặp lúc 9h nhé");

        await Handler().HandleAsync(For(_room, message));

        Assert.Equal(2, _dispatcher.Commands.Count);
        var email = _dispatcher.Commands.Single(c => c.TemplateCode == PlatformDefaults.ChatOfflineEmailTemplate);
        var chat = _dispatcher.Commands.Single(c => c.TemplateCode == PlatformDefaults.ChatOfflineChatTemplate);
        Assert.Equal(("crm", "minh@example.com", null), (email.ProjectCode, email.RecipientEmail, email.RecipientChatId));
        Assert.Equal(("crm", null, "chat-9"), (chat.ProjectCode, chat.RecipientEmail, chat.RecipientChatId));
        Assert.Equal("Lan", email.Data!["SenderName"]);
        Assert.Equal("Hẹn gặp lúc 9h nhé", email.Data["Preview"]);
        Assert.False(email.Queue);
    }

    [Fact]
    public async Task AMemberWithOnlyAnEmail_GetsOnlyTheEmail()
    {
        _room.FindMember("minh")!.SetProfile(null, null, null);
        var onlyEmail = Conversation.CreateDirect("crm", "a", "b", Now);
        onlyEmail.FindMember("b")!.SetProfile(null, "b@example.com", null);
        _repo.Conversations.Add(onlyEmail);
        var message = ChatMessage.Create(onlyEmail.Id, "a", "A", "hi", null, Now.AddSeconds(1));
        _repo.Messages.Add(message);

        await Handler().HandleAsync(For(onlyEmail, message, "b"));

        var command = Assert.Single(_dispatcher.Commands);
        Assert.Equal(PlatformDefaults.ChatOfflineEmailTemplate, command.TemplateCode);
    }

    [Fact]
    public async Task ALongMessage_IsCut_AndAddressesInItAreRemoved_SoTheNoticeIsNotALinkOfTheSenders()
    {
        var message = Say("lan", "Look at https://evil.example/login?token=1 and www.evil.example/x now " + new string('x', 200));

        await Handler().HandleAsync(For(_room, message));

        var preview = _dispatcher.Commands[0].Data!["Preview"];
        Assert.DoesNotContain("evil.example", preview);
        Assert.Contains("[liên kết]", preview);
        Assert.True(preview.Length <= OfflineNotificationHandler.PreviewLength + 1);
        Assert.EndsWith("…", preview);
    }

    [Theory]
    [InlineData("line one\r\nline two\n\n   spaced", "line one line two spaced")]
    [InlineData("<b>bold</b> & more", "<b>bold</b> & more")]   // the template engine encodes it; the preview is plain text
    public void ThePreview_IsASingleLine(string text, string expected)
    {
        var message = ChatMessage.Create(Guid.NewGuid(), "lan", "Lan", text, null, Now);

        Assert.Equal(expected, OfflineNotificationHandler.Preview(message));
    }

    [Fact]
    public void ThePreviewOfAFileWithoutText_SaysSo()
    {
        var message = ChatMessage.Create(Guid.NewGuid(), "lan", "Lan", string.Empty, Guid.NewGuid(), Now);

        Assert.Equal("[tệp đính kèm]", OfflineNotificationHandler.Preview(message));
    }

    // ------------------------------------------------------------------------------------------------ it keeps quiet

    [Fact]
    public async Task ABurstOfMessages_MakesOneNotice_AboutTheNewest()
    {
        var first = Say("lan", "one");
        var second = Say("lan", "two");
        var third = Say("lan", "three");

        await Handler().HandleAsync(For(_room, first));
        await Handler().HandleAsync(For(_room, second));
        Assert.Empty(_dispatcher.Commands);

        await Handler().HandleAsync(For(_room, third));

        Assert.Equal(2, _dispatcher.Commands.Count);   // one per way of reaching the member, all about "three"
        Assert.All(_dispatcher.Commands, c => Assert.Equal("three", c.Data!["Preview"]));
    }

    [Fact]
    public async Task ARepliedConversation_NeedsNoNotice()
    {
        var message = Say("lan", "ping");
        Say("minh", "pong");   // minh answered from somewhere: they are here

        await Handler().HandleAsync(For(_room, message));

        Assert.Empty(_dispatcher.Commands);
    }

    [Fact]
    public async Task AMemberWhoIsBackOnline_IsNotNotified()
    {
        var message = Say("lan", "ping");
        _presence.Connected("crm", "minh", "tab");

        await Handler().HandleAsync(For(_room, message));

        Assert.Empty(_dispatcher.Commands);
    }

    [Fact]
    public async Task AMemberWhoAlreadyRead_IsNotNotified()
    {
        var message = Say("lan", "ping");
        _room.FindMember("minh")!.MarkRead(message.CreatedAt);

        await Handler().HandleAsync(For(_room, message));

        Assert.Empty(_dispatcher.Commands);
    }

    [Fact]
    public async Task AMutedConversation_AMemberWhoLeft_ASystemMessage_AndAGoneConversation_AreAllSilent()
    {
        var message = Say("lan", "ping");
        var system = ChatMessage.System(_room.Id, "someone joined", Now.AddSeconds(50));
        _repo.Messages.Add(system);

        _room.FindMember("minh")!.SetMuted(true);
        await Handler().HandleAsync(For(_room, message));
        _room.FindMember("minh")!.SetMuted(false);
        await Handler().HandleAsync(For(_room, system));
        await Handler().HandleAsync(new OfflineNotificationPayload("crm", Guid.NewGuid(), message.Id, "minh"));
        await Handler().HandleAsync(new OfflineNotificationPayload("crm", _room.Id, Guid.NewGuid(), "minh"));
        await Handler().HandleAsync(new OfflineNotificationPayload("crm", _room.Id, message.Id, "stranger"));
        await Handler().HandleAsync(new OfflineNotificationPayload("linguabridge", _room.Id, message.Id, "minh"));   // another project's id

        Assert.Empty(_dispatcher.Commands);
    }

    // ------------------------------------------------------------------------------------------------ when the notice cannot go

    [Fact]
    public async Task ARefusalThatWaitingCannotCure_FailsTheJobForGood_WhenThereWasNoOtherWay()
    {
        _room.FindMember("minh")!.SetProfile(null, null, null);
        var onlyEmail = Conversation.CreateDirect("crm", "a", "b", Now);
        onlyEmail.FindMember("b")!.SetProfile(null, "b@example.com", null);
        _repo.Conversations.Add(onlyEmail);
        var message = ChatMessage.Create(onlyEmail.Id, "a", "A", "hi", null, Now.AddSeconds(1));
        _repo.Messages.Add(message);
        var quota = new QuotaExceededException(QuotaKind.DailyEmail, 5, 5, Now.AddDays(1), "no quota left");
        var dispatcher = new ThrowingDispatcher(quota);

        var handler = new OfflineNotificationHandler(_repo, _presence, dispatcher, NullLogger<OfflineNotificationHandler>.Instance);

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => handler.HandleAsync(For(onlyEmail, message, "b")));
    }

    [Fact]
    public async Task ADeliveryThatFailed_IsRetriedByTheJobEngine()
    {
        var message = Say("lan", "ping");
        _dispatcher.Respond = _ => FakeNotificationDispatcher.Failed("smtp down");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler().HandleAsync(For(_room, message)));

        Assert.Contains("smtp down", ex.Message);
    }

    [Fact]
    public async Task WhenOneWayWorks_TheOtherFailingDoesNotMakeTheJobRetry_SoTheWorkingOneIsNotSentTwice()
    {
        var message = Say("lan", "ping");
        _dispatcher.Respond = c => c.TemplateCode == PlatformDefaults.ChatOfflineEmailTemplate
            ? FakeNotificationDispatcher.Failed("smtp down")
            : new DispatchNotificationResult(true, "Sent", [], 1, Guid.NewGuid(), []);

        await Handler().HandleAsync(For(_room, message));   // does not throw

        Assert.Equal(2, _dispatcher.Commands.Count);
    }

    // ------------------------------------------------------------------------------------------------ the queue's handler

    [Fact]
    public async Task TheChatQueueHandler_RoutesAnOfflineJobToTheNoticeHandler_AndOtherJobsToTheChatDispatcher()
    {
        var message = Say("lan", "ping");
        var recording = new RecordingOffline();
        var handler = new ChatJobHandler(new NeverUsedChat(), recording);
        var job = BackgroundJobRecord.Create(JobQueues.Chat, ConversationService.OfflineJobType, JobJson.Serialize(For(_room, message)), 4, Now);

        await handler.HandleAsync(job, CancellationToken.None);

        Assert.Equal(message.Id, Assert.Single(recording.Seen).MessageId);
    }

    [Fact]
    public async Task AnOfflineJobWithoutAHandler_FailsForGood()
    {
        var job = BackgroundJobRecord.Create(JobQueues.Chat, ConversationService.OfflineJobType, "{}", 4, Now);

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => new ChatJobHandler(new NeverUsedChat()).HandleAsync(job, CancellationToken.None));
    }

    private sealed class RecordingOffline : IOfflineNotificationHandler
    {
        public List<OfflineNotificationPayload> Seen { get; } = [];

        public Task HandleAsync(OfflineNotificationPayload payload, CancellationToken cancellationToken = default)
        {
            Seen.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDispatcher(Exception ex) : INotificationDispatcher
    {
        public Task<DispatchNotificationResult> DispatchAsync(DispatchNotificationCommand command, CancellationToken cancellationToken = default) => throw ex;

        public Task<IReadOnlyList<NotificationTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<NotificationLogDto>> ListLogsAsync(string? projectCode, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NeverUsedChat : AppInHouse.SharedServices.Application.Notifications.Bots.INotificationBotDispatcher
    {
        public Task<AppInHouse.SharedServices.Application.Notifications.Bots.SendBotMessageResult> SendAsync(AppInHouse.SharedServices.Application.Notifications.Bots.SendBotMessageCommand command, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("an offline job must not reach the chat dispatcher");

        public Task<IReadOnlyList<AppInHouse.SharedServices.Application.Email.DeliveryLogDto>> RecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

public class ChatMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheMonitor_CountsConversationsAndTodaysMessages_ListsWhoIsOnline_AndShowsNoText()
    {
        var repo = new FakeConversationRepository();
        var presence = new UserPresenceTracker();
        var direct = Conversation.CreateDirect("crm", "lan", "minh", Now.AddDays(-2));
        var group = Conversation.CreateGroup("crm", "Team", "lan", Now.AddDays(-2));
        var other = Conversation.CreateGroup("linguabridge", "Other", "an", Now.AddDays(-2));
        repo.Conversations.AddRange([direct, group, other]);
        repo.Messages.AddRange([
            ChatMessage.Create(direct.Id, "lan", "Lan", "yesterday", null, Now.AddDays(-1)),
            ChatMessage.Create(direct.Id, "lan", "Lan", "secret words today", null, Now.AddHours(-1)),
            ChatMessage.Create(group.Id, "lan", "Lan", "today too", null, Now.AddMinutes(-5)),
            ChatMessage.System(group.Id, "someone joined", Now.AddMinutes(-4)),
            ChatMessage.Create(other.Id, "an", "An", "other project", null, Now.AddMinutes(-3))]);
        presence.Connected("crm", "lan", "c1");
        presence.Connected("crm", "lan", "c2");
        presence.Connected("linguabridge", "an", "c3");
        var monitor = new ChatMonitor(repo, presence, new FixedClock(Now));

        var crm = await monitor.GetAsync("crm");
        var all = await monitor.GetAsync(null);

        Assert.Equal((2, 1, 1, 2), (crm.Conversations, crm.DirectConversations, crm.GroupConversations, crm.MessagesToday));
        Assert.Equal((1, 2), (crm.OnlineUsers, crm.Connections));
        Assert.Equal(["lan"], crm.OnlineUserIds);
        Assert.Equal((3, 3), (all.Conversations, all.MessagesToday));
        Assert.Equal((2, 3), (all.OnlineUsers, all.Connections));
        Assert.Equal(["crm:lan", "linguabridge:an"], all.OnlineUserIds);
        Assert.DoesNotContain(crm.Recent, c => c.ProjectCode != "crm");
        Assert.Equal(2, crm.Recent.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(all);
        Assert.DoesNotContain("secret words", json);
    }
}
