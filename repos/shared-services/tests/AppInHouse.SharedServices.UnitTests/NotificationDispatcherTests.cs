using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Notifications;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.UnitTests.Support;

namespace AppInHouse.SharedServices.UnitTests;

public class NotificationDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeNotificationRepository _repository = new();
    private readonly FakeChannelSender _sender = new();
    private readonly FakeJobQueue _jobs = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    private NotificationDispatcher Dispatcher() => new(_repository, _sender, _jobs, _unitOfWork, new FixedClock(Now));

    private NotificationTemplate AddTemplate(
        string code, NotificationChannel channel, NotificationPriority priority = NotificationPriority.Normal,
        string subject = "Hello {{UserName}}", string body = "Hi {{UserName}}, open {{ActionUrl}}")
    {
        var template = NotificationTemplate.Create(code, code, subject, body, channel, priority, Now);
        _repository.Templates.Add(template);
        return template;
    }

    private static Dictionary<string, string> Data(string name = "Lan", string url = "https://app.example.com/verify?a=1&b=2") =>
        new() { ["UserName"] = name, ["ActionUrl"] = url };

    // ------------------------------------------------------------------------------------------------ rendering

    [Fact]
    public async Task AnEmailTemplate_IsFilledIn_AndSentOnceToTheRecipient()
    {
        AddTemplate("Welcome", NotificationChannel.Email);

        var result = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", Data(), RecipientEmail: "lan@example.com"));

        var email = Assert.Single(_sender.Emails);
        Assert.Equal(("crm", "lan@example.com", "Hello Lan"), (email.Project, email.To, email.Subject));
        Assert.Equal("Hi Lan, open https://app.example.com/verify?a=1&b=2", email.Text);
        Assert.Empty(_sender.Chats);
        Assert.True(result.Success);
        Assert.Equal("Sent", result.Status);
    }

    [Fact]
    public async Task InTheHtmlPart_ValuesAreEscaped_AndAddressesBecomeLinks()
    {
        AddTemplate("Welcome", NotificationChannel.Email);

        await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", Data(name: "<script>alert(1)</script>"), RecipientEmail: "lan@example.com"));

        var html = Assert.Single(_sender.Emails).Html;
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("<a href=\"https://app.example.com/verify?a=1&amp;b=2\">https://app.example.com/verify?a=1&amp;b=2</a>", html);
    }

    [Fact]
    public async Task AValueThatLooksLikeAScriptAddress_IsNotMadeIntoALink()
    {
        AddTemplate("Welcome", NotificationChannel.Email);

        await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", Data(url: "javascript:alert(1)"), RecipientEmail: "lan@example.com"));

        Assert.DoesNotContain("<a ", Assert.Single(_sender.Emails).Html);
    }

    [Fact]
    public async Task ASubjectIsOneLine_EvenWhenAValueHasLineBreaks()
    {
        AddTemplate("Welcome", NotificationChannel.Email);

        await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", Data(name: "Lan\r\nBcc: attacker@example.com"), RecipientEmail: "lan@example.com"));

        Assert.DoesNotContain('\n', Assert.Single(_sender.Emails).Subject);
        Assert.DoesNotContain('\r', Assert.Single(_sender.Emails).Subject);
    }

    [Fact]
    public async Task AMissingPlaceholderValue_IsRefused_NamingIt_AndNothingIsSentOrLogged()
    {
        AddTemplate("Welcome", NotificationChannel.Email);

        var error = await Assert.ThrowsAsync<ValidationException>(() => Dispatcher().DispatchAsync(
            new DispatchNotificationCommand("crm", "Welcome", new Dictionary<string, string> { ["UserName"] = "Lan" }, RecipientEmail: "lan@example.com")));

        Assert.Contains("ActionUrl", error.Errors["Data"][0]);
        Assert.Empty(_sender.Emails);
        Assert.Empty(_repository.Logs);
    }

    [Fact]
    public async Task AValueThatContainsAPlaceholder_IsNotExpandedAgain()
    {
        AddTemplate("Welcome", NotificationChannel.Email);

        await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", Data(name: "{{ActionUrl}}"), RecipientEmail: "lan@example.com"));

        Assert.Equal("Hi {{ActionUrl}}, open https://app.example.com/verify?a=1&b=2", Assert.Single(_sender.Emails).Text);
    }

    [Fact]
    public async Task TheStockTemplates_RenderWithTheirOwnPlaceholders()
    {
        foreach (var (code, name, subject, body, channel, priority) in PlatformDefaults.Templates)
            _repository.Templates.Add(NotificationTemplate.Create(code, name, subject, body, channel, priority, Now));
        var dispatcher = Dispatcher();

        var listed = await dispatcher.ListTemplatesAsync();

        Assert.Equal(5, listed.Count);
        foreach (var template in listed)
        {
            _sender.Emails.Clear();
            var values = template.Placeholders.ToDictionary(p => p, p => "v-" + p);
            await dispatcher.DispatchAsync(new DispatchNotificationCommand("crm", template.Code, values, RecipientEmail: "lan@example.com"));
            Assert.DoesNotContain("{{", _sender.Emails.Count > 0 ? _sender.Emails[0].Text : _sender.Chats[0].Text);
        }
    }

    // ------------------------------------------------------------------------------------------------ channels

    [Fact]
    public async Task AChatTemplate_GoesToChatOnly_WithTheSubjectAboveTheBody_AndTheDefaultChatWhenNoIdIsGiven()
    {
        AddTemplate("Ping", NotificationChannel.Chat);

        await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Ping", Data()));

        Assert.Empty(_sender.Emails);
        var chat = Assert.Single(_sender.Chats);
        Assert.Null(chat.Recipient);
        Assert.StartsWith("Hello Lan\nHi Lan", chat.Text);
    }

    [Fact]
    public async Task ABothTemplate_SendsEmailAndChatAtTheSameTime()
    {
        AddTemplate("Alert", NotificationChannel.Both);
        _sender.Gate = new TaskCompletionSource();

        var dispatch = Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data(), "lan@example.com", "12345"));
        await Task.Delay(100);

        // Both were asked while neither had answered: they ran side by side, not one after the other.
        Assert.Single(_sender.Emails);
        Assert.Single(_sender.Chats);
        Assert.False(dispatch.IsCompleted);

        _sender.Gate.SetResult();
        var result = await dispatch;

        Assert.Equal("Sent", result.Status);
        Assert.Equal(["Email", "Chat"], result.Channels.Select(c => c.Channel));
        Assert.Equal("12345", _sender.Chats[0].Recipient);
    }

    [Fact]
    public async Task WhenOneOfTwoChannelsFails_ItIsPartial_StillASuccess_AndThereIsNoFallback()
    {
        AddTemplate("Alert", NotificationChannel.Both, NotificationPriority.Critical);
        _sender.ChatResult = () => FakeChannelSender.Failure("bot blocked");

        var result = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data(), "lan@example.com"));

        Assert.True(result.Success);
        Assert.Equal("Partial", result.Status);
        Assert.Single(_sender.Emails);
        Assert.Single(_sender.Chats);
        Assert.DoesNotContain(result.Channels, c => c.IsFallback);
        Assert.Equal("bot blocked", result.Channels.Single(c => !c.Success).Error);
    }

    [Fact]
    public async Task AnEmailTemplate_NeedsTheRecipientsAddress_AChatOneDoesNot()
    {
        AddTemplate("Welcome", NotificationChannel.Email);
        AddTemplate("Ping", NotificationChannel.Chat);

        var error = await Assert.ThrowsAsync<ValidationException>(() => Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", Data())));
        await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Ping", Data()));

        Assert.True(error.Errors.ContainsKey("RecipientEmail"));
        Assert.Single(_sender.Chats);
    }

    // ------------------------------------------------------------------------------------------------ fallback

    [Fact]
    public async Task ACriticalEmailThatFails_IsSummarisedToChat_WithoutTheBody()
    {
        AddTemplate("Reset", NotificationChannel.Email, NotificationPriority.Critical, "Reset for {{UserName}}", "One-time link: {{ActionUrl}}");
        _sender.EmailResult = () => FakeChannelSender.Failure("smtp refused");

        var result = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Reset", Data(url: "https://app.example.com/reset?token=SECRET123"), "lan@example.com", "ops-chat"));

        Assert.True(result.Success);
        Assert.Equal("Fallback", result.Status);
        var chat = Assert.Single(_sender.Chats);
        Assert.Equal("ops-chat", chat.Recipient);
        Assert.Contains("Reset for Lan", chat.Text);
        Assert.Contains("smtp refused", chat.Text);
        Assert.DoesNotContain("SECRET123", chat.Text);          // the body may carry a one-time link: only a summary leaves by the other channel
        Assert.DoesNotContain("lan@example.com", chat.Text);
        Assert.Equal(["Email", "Chat"], result.Channels.Select(c => c.Channel));
        Assert.True(result.Channels[1].IsFallback);
        Assert.False(result.Channels[0].Success);
    }

    [Fact]
    public async Task AHighEmailThatFails_AlsoFallsBack_ButANormalOneDoesNot()
    {
        AddTemplate("High", NotificationChannel.Email, NotificationPriority.High);
        AddTemplate("Normal", NotificationChannel.Email, NotificationPriority.Normal);
        AddTemplate("Low", NotificationChannel.Email, NotificationPriority.Low);
        _sender.EmailResult = () => FakeChannelSender.Failure("down");

        var high = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "High", Data(), "lan@example.com"));
        var chatsAfterHigh = _sender.Chats.Count;
        var normal = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Normal", Data(), "lan@example.com"));
        var low = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Low", Data(), "lan@example.com"));

        Assert.Equal("Fallback", high.Status);
        Assert.Equal(1, chatsAfterHigh);
        Assert.Equal(("Failed", false), (normal.Status, normal.Success));
        Assert.Equal(("Failed", false), (low.Status, low.Success));
        Assert.Single(_sender.Chats);
    }

    [Fact]
    public async Task ACallerWithoutTheScopeOfAChannelTheTemplateUses_IsRefused_BeforeAnythingIsSentQueuedOrLogged()
    {
        AddTemplate("Mail", NotificationChannel.Email);
        AddTemplate("Chat", NotificationChannel.Chat);
        AddTemplate("Both", NotificationChannel.Both);

        async Task Refused(string code, bool email, bool chat, bool queue, string channel)
        {
            var ex = await Assert.ThrowsAsync<ChannelNotPermittedException>(() => Dispatcher().DispatchAsync(
                new DispatchNotificationCommand("crm", code, Data(), "lan@example.com", Queue: queue, CanSendEmail: email, CanSendChat: chat)));
            Assert.Equal(channel, ex.Channel);
        }

        foreach (var queue in new[] { false, true })
        {
            await Refused("Mail", email: false, chat: true, queue, "email");
            await Refused("Chat", email: true, chat: false, queue, "chat");
            await Refused("Both", email: true, chat: false, queue, "chat");
            await Refused("Both", email: false, chat: true, queue, "email");
        }

        Assert.Empty(_sender.Emails);
        Assert.Empty(_sender.Chats);
        Assert.Empty(_jobs.Jobs);
        Assert.Empty(_repository.Logs);
    }

    [Fact]
    public async Task TheFallbackChannel_IsNotUsed_WhenTheCallerMayNotSendThrough_It()
    {
        AddTemplate("Reset", NotificationChannel.Email, NotificationPriority.Critical);
        AddTemplate("Alert", NotificationChannel.Chat, NotificationPriority.Critical);
        _sender.EmailResult = () => FakeChannelSender.Failure("down");
        _sender.ChatResult = () => FakeChannelSender.Failure("down");

        var mail = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Reset", Data(), "lan@example.com", CanSendChat: false));
        var chat = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data(), "lan@example.com", CanSendEmail: false));

        Assert.Equal("Failed", mail.Status);
        Assert.Equal("Failed", chat.Status);
        Assert.Single(_sender.Emails);   // only the primary email attempt, no fallback email
        Assert.Single(_sender.Chats);    // only the primary chat attempt, no summary
    }

    [Fact]
    public async Task ThePriorityOfTheRequest_CanRaiseANormalTemplateToHigh()
    {
        AddTemplate("Normal", NotificationChannel.Email, NotificationPriority.Normal);
        _sender.EmailResult = () => FakeChannelSender.Failure("down");

        var result = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Normal", Data(), "lan@example.com", Priority: NotificationPriority.High));

        Assert.Equal("Fallback", result.Status);
    }

    [Fact]
    public async Task AChatThatFails_FallsBackToTheFullEmail_OnlyWhenTheRecipientGaveAnAddress()
    {
        AddTemplate("Alert", NotificationChannel.Chat, NotificationPriority.Critical);
        _sender.ChatResult = () => FakeChannelSender.Failure("chat down");

        var withEmail = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data(), "lan@example.com"));
        var withoutEmail = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data()));

        Assert.Equal("Fallback", withEmail.Status);
        var email = Assert.Single(_sender.Emails);
        Assert.Equal("lan@example.com", email.To);
        Assert.Contains("https://app.example.com/verify", email.Text);   // the recipient gets the notification itself
        Assert.Equal("Failed", withoutEmail.Status);
    }

    [Fact]
    public async Task WhenTheFallbackFailsToo_TheNotificationFailed()
    {
        AddTemplate("Alert", NotificationChannel.Email, NotificationPriority.Critical);
        _sender.EmailResult = () => FakeChannelSender.Failure("email down");
        _sender.ChatResult = () => FakeChannelSender.Failure("chat down");

        var result = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data(), "lan@example.com"));

        Assert.False(result.Success);
        Assert.Equal("Failed", result.Status);
        Assert.Equal(2, result.Channels.Count);
        Assert.Equal("Email→Chat (fallback)", _repository.Logs.Single().Channels);
    }

    // ------------------------------------------------------------------------------------------------ the log, quotas, the queue

    [Fact]
    public async Task EveryDispatch_IsLogged_WithItsChannelsStatusAndRecipient()
    {
        AddTemplate("Welcome", NotificationChannel.Email);

        var result = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", Data(), "lan@example.com"));

        var log = Assert.Single(_repository.Logs);
        Assert.Equal(result.LogId, log.Id);
        Assert.Equal(("crm", "Welcome", "Email", "Sent", "lan@example.com"), (log.ProjectCode, log.TemplateCode, log.Channels, log.Status, log.Recipient));
        Assert.Equal(1, _unitOfWork.Saves);
    }

    [Fact]
    public async Task WhenEveryChannelStoppedOnTheDailyQuota_TheAnswerIsTheQuotaError_AfterTheDispatchWasLogged()
    {
        AddTemplate("Alert", NotificationChannel.Both);
        var quota = new QuotaExceededException(QuotaKind.DailyEmail, 10, 10, Now.AddHours(14), "daily quota used");
        _sender.EmailResult = () => new ChannelSendOutcome(false, quota.Message, null, quota);
        _sender.ChatResult = () => new ChannelSendOutcome(false, quota.Message, null, quota);

        var thrown = await Assert.ThrowsAsync<QuotaExceededException>(() => Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data(), "lan@example.com")));

        Assert.Same(quota, thrown);
        Assert.Equal("Failed", Assert.Single(_repository.Logs).Status);
    }

    [Fact]
    public async Task WhenOnlyOneChannelStoppedOnTheQuota_TheOtherStillDelivers_NoErrorIsThrown()
    {
        AddTemplate("Alert", NotificationChannel.Both);
        var quota = new QuotaExceededException(QuotaKind.DailyEmail, 10, 10, Now.AddHours(14), "daily quota used");
        _sender.EmailResult = () => new ChannelSendOutcome(false, quota.Message, null, quota);

        var result = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data(), "lan@example.com"));

        Assert.Equal("Partial", result.Status);
    }

    [Fact]
    public async Task AQuotaStop_IsNotRetriedOnTheOtherChannel_EvenForACriticalMessage()
    {
        AddTemplate("Alert", NotificationChannel.Email, NotificationPriority.Critical);
        var quota = new QuotaExceededException(QuotaKind.DailyEmail, 10, 10, Now.AddHours(14), "daily quota used");
        _sender.EmailResult = () => new ChannelSendOutcome(false, quota.Message, null, quota);

        await Assert.ThrowsAsync<QuotaExceededException>(() => Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data(), "lan@example.com")));

        Assert.Empty(_sender.Chats);
    }

    [Fact]
    public async Task InQueueMode_NothingIsSentNow_ButOneJobPerChannelIsQueued()
    {
        AddTemplate("Alert", NotificationChannel.Both);

        var result = await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Alert", Data(), "lan@example.com", "ops", Queue: true));

        Assert.Empty(_sender.Emails);
        Assert.Empty(_sender.Chats);
        Assert.Equal("Queued", result.Status);
        Assert.Equal(2, result.JobIds.Count);
        var email = Assert.IsType<EmailJobPayload>(_jobs.Jobs.Single(j => j.Queue == JobQueues.Email).Payload);
        var chat = Assert.IsType<ChatJobPayload>(_jobs.Jobs.Single(j => j.Queue == JobQueues.Chat).Payload);
        Assert.Equal(("crm", "lan@example.com", "Hello Lan"), (email.Project, email.To, email.Subject));
        Assert.Equal("ops", chat.Recipient);
        Assert.Equal("Queued", Assert.Single(_repository.Logs).Status);
    }

    [Fact]
    public async Task InQueueMode_AnEmailTemplateQueuesOnlyAnEmail()
    {
        AddTemplate("Welcome", NotificationChannel.Email);

        await Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", Data(), "lan@example.com", Queue: true));

        Assert.Equal(JobQueues.Email, Assert.Single(_jobs.Jobs).Queue);
    }

    // ------------------------------------------------------------------------------------------------ what is refused

    [Fact]
    public async Task AnUnknownTemplate_IsNotFound_AnInactiveOneIsAConflict()
    {
        var off = AddTemplate("Off", NotificationChannel.Email);
        off.SetActive(false);

        await Assert.ThrowsAsync<NotFoundException>(() => Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Nope", Data(), "lan@example.com")));
        await Assert.ThrowsAsync<DomainException>(() => Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Off", Data(), "lan@example.com")));
        Assert.Empty(_sender.Emails);
    }

    [Theory]
    [InlineData("", "Welcome", "lan@example.com")]
    [InlineData("Bad Project", "Welcome", "lan@example.com")]
    [InlineData("crm", "", "lan@example.com")]
    [InlineData("crm", "not valid!", "lan@example.com")]
    [InlineData("crm", "Welcome", "not-an-address")]
    public async Task ABadRequest_IsRefusedBeforeAnythingIsLookedUp(string project, string code, string email)
    {
        AddTemplate("Welcome", NotificationChannel.Email);

        await Assert.ThrowsAsync<ValidationException>(() => Dispatcher().DispatchAsync(new DispatchNotificationCommand(project, code, Data(), email)));
        Assert.Empty(_sender.Emails);
    }

    [Fact]
    public async Task TooManyValues_OrATooLongOne_IsRefused()
    {
        AddTemplate("Welcome", NotificationChannel.Email);
        var many = Enumerable.Range(0, NotificationDispatcher.MaxDataEntries + 1).ToDictionary(i => "K" + i, _ => "v");
        var tooLong = new Dictionary<string, string> { ["UserName"] = new string('x', NotificationDispatcher.MaxDataValueLength + 1), ["ActionUrl"] = "u" };

        await Assert.ThrowsAsync<ValidationException>(() => Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", many, "lan@example.com")));
        await Assert.ThrowsAsync<ValidationException>(() => Dispatcher().DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", tooLong, "lan@example.com")));
    }

    [Fact]
    public async Task TheLogsOfAProject_AreOnlyItsOwn()
    {
        AddTemplate("Welcome", NotificationChannel.Email);
        var dispatcher = Dispatcher();
        await dispatcher.DispatchAsync(new DispatchNotificationCommand("crm", "Welcome", Data(), "a@example.com"));
        await dispatcher.DispatchAsync(new DispatchNotificationCommand("identity", "Welcome", Data(), "b@example.com"));

        var crm = await dispatcher.ListLogsAsync("CRM", 10);
        var all = await dispatcher.ListLogsAsync(null, 10);

        Assert.Equal("crm", Assert.Single(crm).ProjectCode);
        Assert.Equal(2, all.Count);
    }
}
