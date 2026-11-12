using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Api.Chat;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using static AppInHouse.SharedServices.IntegrationTests.Support.ChatApi;
using static AppInHouse.SharedServices.IntegrationTests.Support.PlatformApi;

namespace AppInHouse.SharedServices.IntegrationTests;

public class ConversationApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Monitor = "/api/v1/chat/monitor";

    // ------------------------------------------------------------------------------------------------ who may call

    [Fact]
    public async Task WithoutAKey_401_WithoutTheScope_403_WithoutSayingWhoIsChatting_400()
    {
        var project = NewProject();
        var noScope = factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, ApiScopes.ChatSend, ApiScopes.ConfigRead, ApiScopes.EmailSend)).PlainKey);
        noScope.DefaultRequestHeaders.Add("X-Chat-User", "lan");
        var key = await ProjectKeyAsync(factory, project);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).GetAsync(Conversations)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noScope.GetAsync(Conversations)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.ClientWithKey(key).GetAsync(Conversations)).StatusCode);   // no X-Chat-User
        foreach (var bad in new[] { "has space", "semi;colon", new string('x', 101) })
        {
            var client = factory.ClientWithKey(key);
            client.DefaultRequestHeaders.Add("X-Chat-User", bad);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Conversations)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await User(factory, key, "lan").GetAsync(Conversations)).StatusCode);
    }

    [Fact]
    public async Task AKey_ChatsOnlyForItsOwnProject_ThePlatformMayNameAnother()
    {
        var mine = NewProject();
        var other = NewProject();
        var myKey = await ProjectKeyAsync(factory, mine);
        var platformKey = await ProjectKeyAsync(factory, "default");
        var room = await CreateDirectAsync(User(factory, await ProjectKeyAsync(factory, other), "lan"), "minh");

        var naming = await User(factory, myKey, "lan").GetAsync($"{Conversations}?projectCode={other}");
        var platform = await User(factory, platformKey, "lan").GetAsync($"{Conversations}?projectCode={other}");
        var platformRoom = await User(factory, platformKey, "lan").GetAsync($"{Conversations}/{IdOf(room)}/messages?projectCode={other}");

        Assert.Equal(HttpStatusCode.Forbidden, naming.StatusCode);
        Assert.Equal(HttpStatusCode.OK, platform.StatusCode);
        Assert.Single((await ReadAsync(platform)).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, platformRoom.StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ creating

    [Fact]
    public async Task ADirectConversation_IsCreatedOnce_WhoeverStartsIt()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lan = User(factory, key, "lan");
        var minh = User(factory, key, "minh");

        var first = await lan.PostAsJsonAsync(Conversations, new { type = "Direct", members = new[] { new { userId = "minh" } } });
        var again = await minh.PostAsJsonAsync(Conversations, new { type = "Direct", members = new[] { new { userId = "lan" } } });
        var third = await lan.PostAsJsonAsync(Conversations, new { type = "Direct", members = new[] { new { userId = "minh" } } });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var id = IdOf(await ReadAsync(first));
        Assert.Equal(id, IdOf(await ReadAsync(again)));
        Assert.Equal(id, IdOf(await ReadAsync(third)));
        Assert.Equal(1, await factory.WithDbAsync(db => db.Conversations.CountAsync(c => c.ProjectCode == project)));
    }

    [Fact]
    public async Task EightRequestsAtOnce_ForTheSamePair_MakeOneConversation()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            var client = User(factory, key, i % 2 == 0 ? "lan" : "minh");
            var other = i % 2 == 0 ? "minh" : "lan";
            var response = await client.PostAsJsonAsync(Conversations, new { type = "Direct", members = new[] { new { userId = other } } });
            var body = await ReadAsync(response);
            return (response.StatusCode, Id: body.TryGetProperty("id", out var id) ? id.GetGuid() : Guid.Empty, Body: body.ToString());
        }));

        Assert.All(answers, a => Assert.True(a.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, a.Body));
        Assert.Single(answers.Select(a => a.Id).Distinct());
        Assert.Equal(1, await factory.WithDbAsync(db => db.Conversations.CountAsync(c => c.ProjectCode == project)));
    }

    [Fact]
    public async Task TheSamePairOfIds_InTwoProjects_AreTwoConversations()
    {
        var a = NewProject();
        var b = NewProject();

        var inA = await CreateDirectAsync(User(factory, await ProjectKeyAsync(factory, a), "lan"), "minh");
        var inB = await CreateDirectAsync(User(factory, await ProjectKeyAsync(factory, b), "lan"), "minh");

        Assert.NotEqual(IdOf(inA), IdOf(inB));
    }

    [Fact]
    public async Task BadRequests_Are400_AndNothingIsCreated()
    {
        var project = NewProject();
        var lan = User(factory, await ProjectKeyAsync(factory, project), "lan");

        var responses = new[]
        {
            await lan.PostAsJsonAsync(Conversations, new { type = "Direct", members = new[] { new { userId = "lan" } } }),               // with yourself
            await lan.PostAsJsonAsync(Conversations, new { type = "Direct", members = Array.Empty<object>() }),                           // nobody
            await lan.PostAsJsonAsync(Conversations, new { type = "Direct", members = new[] { new { userId = "a" }, new { userId = "b" } } }),   // two others
            await lan.PostAsJsonAsync(Conversations, new { type = "Group", title = " ", members = new[] { new { userId = "minh" } } }),   // no title
            await lan.PostAsJsonAsync(Conversations, new { type = "Group", title = "T", members = new[] { new { userId = "bad id" } } }),  // bad id
            await lan.PostAsJsonAsync(Conversations, new { type = "Channel", title = "T" }),                                             // unknown type
            await lan.PostAsync(Conversations, new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json")),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Conversations.CountAsync(c => c.ProjectCode == project)));
    }

    // ------------------------------------------------------------------------------------------------ messages and history

    [Fact]
    public async Task ASenderClaimedInTheBody_IsIgnored_TheSenderIsAlwaysTheCaller()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lan = User(factory, key, "lan");
        var minh = User(factory, key, "minh");
        var room = IdOf(await CreateDirectAsync(lan, "minh"));

        var response = await lan.PostAsJsonAsync($"{Conversations}/{room}/messages",
            new { content = "I am not Minh", senderId = "minh", senderName = "Minh", userId = "minh", createdAt = "2001-01-01T00:00:00Z" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = await ReadAsync(response);
        Assert.Equal("lan", sent.GetProperty("senderId").GetString());
        Assert.NotEqual("Minh", sent.GetProperty("senderName").GetString());
        Assert.True(sent.GetProperty("createdAt").GetDateTimeOffset() > new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var history = await ReadAsync(await minh.GetAsync($"{Conversations}/{room}/messages"));
        Assert.All(history.GetProperty("items").EnumerateArray(), m => Assert.Equal("lan", m.GetProperty("senderId").GetString()));
    }

    [Fact]
    public async Task AMessage_IsSaved_ShownInTheHistory_AndCountedAsUnreadUntilRead()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lan = User(factory, key, "lan", "Lan Nguyễn");
        var minh = User(factory, key, "minh");
        var room = IdOf(await CreateDirectAsync(lan, "minh"));

        var sent = await SendAsync(lan, room, "Chào Minh 👋");

        Assert.Equal(("lan", "Lan Nguyễn", "Text", "Chào Minh 👋"), (sent.GetProperty("senderId").GetString(), sent.GetProperty("senderName").GetString(), sent.GetProperty("type").GetString(), sent.GetProperty("content").GetString()));
        var history = await ReadAsync(await minh.GetAsync($"{Conversations}/{room}/messages"));
        Assert.Equal("Chào Minh 👋", Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("content").GetString());
        Assert.False(history.GetProperty("hasMore").GetBoolean());

        var list = (await ReadAsync(await minh.GetAsync(Conversations))).EnumerateArray().Single();
        Assert.Equal((1, "Chào Minh 👋"), (list.GetProperty("unreadCount").GetInt32(), list.GetProperty("lastMessage").GetProperty("content").GetString()));
        Assert.Equal(0, (await ReadAsync(await lan.GetAsync(Conversations))).EnumerateArray().Single().GetProperty("unreadCount").GetInt32());   // the sender wrote it

        var read = await minh.PostAsync($"{Conversations}/{room}/read", null);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(0, (await ReadAsync(await minh.GetAsync(Conversations))).EnumerateArray().Single().GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task TheHistory_IsPagedBackwardsFromACursor_30ByDefault()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lan = User(factory, key, "lan");
        var room = IdOf(await CreateDirectAsync(lan, "minh"));
        for (var i = 0; i < 35; i++) await SendAsync(lan, room, $"m{i:D2}");

        var first = await ReadAsync(await lan.GetAsync($"{Conversations}/{room}/messages"));
        var before = Uri.EscapeDataString(first.GetProperty("nextBefore").GetString()!);
        var beforeId = first.GetProperty("nextBeforeId").GetGuid();
        var second = await ReadAsync(await lan.GetAsync($"{Conversations}/{room}/messages?before={before}&beforeId={beforeId}"));
        var small = await ReadAsync(await lan.GetAsync($"{Conversations}/{room}/messages?limit=5"));

        var firstPage = first.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("content").GetString()).ToList();
        var secondPage = second.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("content").GetString()).ToList();
        Assert.Equal((30, true), (firstPage.Count, first.GetProperty("hasMore").GetBoolean()));
        Assert.Equal("m34", firstPage[0]);
        Assert.Equal((5, false), (secondPage.Count, second.GetProperty("hasMore").GetBoolean()));
        Assert.Equal(["m04", "m03", "m02", "m01", "m00"], secondPage);
        Assert.Equal(5, small.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task BadMessages_Are400_NothingIsSaved()
    {
        var project = NewProject();
        var lan = User(factory, await ProjectKeyAsync(factory, project), "lan");
        var room = IdOf(await CreateDirectAsync(lan, "minh"));

        var empty = await lan.PostAsJsonAsync($"{Conversations}/{room}/messages", new { content = "  " });
        var none = await lan.PostAsJsonAsync($"{Conversations}/{room}/messages", new { });
        var tooLong = await lan.PostAsJsonAsync($"{Conversations}/{room}/messages", new { content = new string('a', 4001) });
        var atLimit = await lan.PostAsJsonAsync($"{Conversations}/{room}/messages", new { content = new string('a', 4000) });
        var foreignFile = await lan.PostAsJsonAsync($"{Conversations}/{room}/messages", new { content = "see", attachmentFileId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignFile.StatusCode);
        Assert.Equal(1, await factory.WithDbAsync(db => db.ChatMessages.CountAsync(m => m.ConversationId == room)));
    }

    // ------------------------------------------------------------------------------------------------ authorization of the room

    [Fact]
    public async Task APersonOutsideTheRoom_GetsA403_ForEverythingInIt_AndLeavesNoTrace()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lan = User(factory, key, "lan");
        var eve = User(factory, key, "eve");
        var room = IdOf(await CreateDirectAsync(lan, "minh"));
        await SendAsync(lan, room, "private");
        var members = await factory.WithDbAsync(db => db.ConversationMembers.CountAsync(m => m.ConversationId == room));

        var responses = new[]
        {
            await eve.GetAsync($"{Conversations}/{room}"),
            await eve.GetAsync($"{Conversations}/{room}/messages"),
            await eve.PostAsJsonAsync($"{Conversations}/{room}/messages", new { content = "let me in" }),
            await eve.PostAsync($"{Conversations}/{room}/read", null),
            await eve.PutAsJsonAsync($"{Conversations}/{room}", new { title = "mine" }),
            await eve.PostAsJsonAsync($"{Conversations}/{room}/members", new { members = new[] { new { userId = "eve" } } }),
            await eve.DeleteAsync($"{Conversations}/{room}/members/lan"),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.DoesNotContain("private", await responses[1].Content.ReadAsStringAsync());
        Assert.Equal(1, await factory.WithDbAsync(db => db.ChatMessages.CountAsync(m => m.ConversationId == room)));
        Assert.Equal(members, await factory.WithDbAsync(db => db.ConversationMembers.CountAsync(m => m.ConversationId == room)));
        Assert.Empty((await ReadAsync(await eve.GetAsync(Conversations))).EnumerateArray());
    }

    [Fact]
    public async Task ARoomOfAnotherProject_IsNotFound_EvenForTheSameUserId()
    {
        var a = NewProject();
        var b = NewProject();
        var room = IdOf(await CreateDirectAsync(User(factory, await ProjectKeyAsync(factory, a), "lan"), "minh"));
        var lanOfB = User(factory, await ProjectKeyAsync(factory, b), "lan");

        Assert.Equal(HttpStatusCode.NotFound, (await lanOfB.GetAsync($"{Conversations}/{room}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lanOfB.PostAsJsonAsync($"{Conversations}/{room}/messages", new { content = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lanOfB.GetAsync($"{Conversations}/{Guid.NewGuid()}/messages")).StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.ChatMessages.CountAsync(m => m.ConversationId == room)));
    }

    [Fact]
    public async Task OnlyTheOwnerAndAdmins_ChangeAGroup_AnyoneMayLeave()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lan = User(factory, key, "lan");
        var minh = User(factory, key, "minh");
        var an = User(factory, key, "an");
        var room = IdOf(await CreateGroupAsync(lan, "Team", "minh", "an"));

        Assert.Equal(HttpStatusCode.Forbidden, (await minh.PostAsJsonAsync($"{Conversations}/{room}/members", new { members = new[] { new { userId = "bao" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await minh.PutAsJsonAsync($"{Conversations}/{room}", new { title = "Hijacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await minh.DeleteAsync($"{Conversations}/{room}/members/an")).StatusCode);

        var added = await lan.PostAsJsonAsync($"{Conversations}/{room}/members", new { members = new[] { new { userId = "bao" }, new { userId = "an" } } });   // an is already in: ignored
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(4, (await ReadAsync(added)).GetProperty("members").GetArrayLength());
        var renamed = await lan.PutAsJsonAsync($"{Conversations}/{room}", new { title = "Renamed" });
        Assert.Equal("Renamed", (await ReadAsync(renamed)).GetProperty("title").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await an.DeleteAsync($"{Conversations}/{room}/members/lan")).StatusCode);   // not the owner's to remove
        Assert.Equal(HttpStatusCode.NoContent, (await minh.DeleteAsync($"{Conversations}/{room}/members/minh")).StatusCode);   // leaving
        Assert.Equal(HttpStatusCode.NoContent, (await lan.DeleteAsync($"{Conversations}/{room}/members/an")).StatusCode);   // the owner removes
        Assert.Equal(HttpStatusCode.NotFound, (await lan.DeleteAsync($"{Conversations}/{room}/members/ghost")).StatusCode);

        Assert.Equal(["bao", "lan"], await factory.WithDbAsync(db => db.ConversationMembers.Where(m => m.ConversationId == room).Select(m => m.UserId).OrderBy(u => u).ToListAsync()));
        Assert.Equal(HttpStatusCode.Forbidden, (await minh.GetAsync($"{Conversations}/{room}/messages")).StatusCode);   // out of the room: out of the history
        var announcements = await ReadAsync(await lan.GetAsync($"{Conversations}/{room}/messages"));
        Assert.Contains(announcements.GetProperty("items").EnumerateArray(), m => m.GetProperty("type").GetString() == "System" && m.GetProperty("content").GetString()!.Contains("rời nhóm"));
    }

    [Fact]
    public async Task ADirectConversation_CannotGainOrLoseMembers_OrBeRenamed()
    {
        var project = NewProject();
        var lan = User(factory, await ProjectKeyAsync(factory, project), "lan");
        var room = IdOf(await CreateDirectAsync(lan, "minh"));

        Assert.Equal(HttpStatusCode.BadRequest, (await lan.PostAsJsonAsync($"{Conversations}/{room}/members", new { members = new[] { new { userId = "bao" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lan.DeleteAsync($"{Conversations}/{room}/members/minh")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await lan.PutAsJsonAsync($"{Conversations}/{room}", new { title = "T" })).StatusCode);
        Assert.Equal(2, await factory.WithDbAsync(db => db.ConversationMembers.CountAsync(m => m.ConversationId == room)));
    }

    // ------------------------------------------------------------------------------------------------ what the portal sees

    [Fact]
    public async Task TheMonitor_ShowsCountsAndTitles_NeverWhatWasSaid_AndEachProjectSeesItsOwn()
    {
        var project = NewProject();
        var other = NewProject();
        var key = await ProjectKeyAsync(factory, project, ApiScopes.ConfigRead);
        var lan = User(factory, key, "lan");
        await CreateGroupAsync(lan, "Team LinguaBridge", "minh");
        var direct = IdOf(await CreateDirectAsync(lan, "an"));
        await SendAsync(lan, direct, "very private words");
        await SendAsync(lan, direct, "more private words");
        var otherKey = await ProjectKeyAsync(factory, other, ApiScopes.ConfigRead);
        await SendAsync(User(factory, otherKey, "x"), IdOf(await CreateDirectAsync(User(factory, otherKey, "x"), "y")), "other project words");
        var admin = await AdminAsync(factory);

        var mine = await factory.ClientWithKey(key).GetAsync($"{Monitor}");
        var ownBody = await mine.Content.ReadAsStringAsync();
        var asked = await factory.ClientWithKey(key).GetAsync($"{Monitor}?projectCode={other}");
        var platform = await ReadAsync(await admin.GetAsync($"{Monitor}?projectCode={project}"));

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var monitor = JsonDocument.Parse(ownBody).RootElement;
        Assert.Equal((2, 1, 1, 2), (monitor.GetProperty("conversations").GetInt32(), monitor.GetProperty("directConversations").GetInt32(), monitor.GetProperty("groupConversations").GetInt32(), monitor.GetProperty("messagesToday").GetInt32()));
        Assert.Contains(monitor.GetProperty("recent").EnumerateArray(), c => c.GetProperty("title").GetString() == "Team LinguaBridge" && c.GetProperty("memberCount").GetInt32() == 2);
        Assert.DoesNotContain("private words", ownBody);
        Assert.DoesNotContain("other project", ownBody);
        Assert.Equal(HttpStatusCode.Forbidden, asked.StatusCode);
        Assert.Equal(2, platform.GetProperty("conversations").GetInt32());
    }

    [Fact]
    public async Task TheMonitor_NeedsTheConfigReadScope()
    {
        var project = NewProject();
        var chatOnly = User(factory, await ProjectKeyAsync(factory, project), "lan");

        Assert.Equal(HttpStatusCode.Forbidden, (await chatOnly.GetAsync(Monitor)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(null).GetAsync(Monitor)).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ the offline notice, through the real job engine

    [Fact]
    public async Task AKeyThatCanOnlyChat_CannotMakeTheServiceSendEmailOrBotMessages_ToAnAddressItChose()
    {
        var project = NewProject();
        var to = NewRecipient();
        var chatId = "chat-" + Guid.NewGuid().ToString("N")[..8];
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, project);
        await ActivateMockChatAsync(admin, project);
        var lan = User(factory, await ProjectKeyAsync(factory, project), "lan", "Lan");               // chat:converse and nothing else
        var room = IdOf(await CreateDirectAsync(lan, "minh", email: to, chatId: chatId));

        await SendAsync(lan, room, "ping");
        await Task.Delay(4000);   // the grace period of the tests is one second: ample time for a notice to go out

        Assert.DoesNotContain(factory.MockMail.SentMessages, m => m.To == to);
        Assert.DoesNotContain(MockChat(factory).SentMessages, m => m.Recipient == chatId);
        Assert.Equal(0, await factory.WithDbAsync(db => db.NotificationLogs.CountAsync(l => l.ProjectCode == project)));
    }

    [Fact]
    public async Task AKeyWithEmailSendOnly_GetsTheEmailNotice_AndNeverTheBotOne()
    {
        var project = NewProject();
        var to = NewRecipient();
        var chatId = "chat-" + Guid.NewGuid().ToString("N")[..8];
        var admin = await AdminAsync(factory);
        await ActivateMockEmailAsync(admin, project);
        await ActivateMockChatAsync(admin, project);
        var lan = User(factory, await ProjectKeyAsync(factory, project, ApiScopes.EmailSend), "lan", "Lan");
        var room = IdOf(await CreateDirectAsync(lan, "minh", email: to, chatId: chatId));

        await SendAsync(lan, room, "ping");

        Assert.True(await UntilAsync(() => factory.MockMail.SentMessages.Any(m => m.To == to)));
        await Task.Delay(2000);
        Assert.DoesNotContain(MockChat(factory).SentMessages, m => m.Recipient == chatId);
    }

    [Fact]
    public async Task AMessageToAnOfflineMember_IsFollowedByAnEmail_AfterTheGracePeriod_WithTheSenderAndAPreview()
    {
        var project = NewProject();
        var to = NewRecipient();
        await ActivateMockEmailAsync(await AdminAsync(factory), project);
        var key = await ProjectKeyAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ChatSend);
        var lan = User(factory, key, "lan", "Lan Nguyễn");
        var room = IdOf(await CreateDirectAsync(lan, "minh", email: to));

        await SendAsync(lan, room, "Họp lúc 9h, xem https://evil.example/login nhé");

        Assert.True(await UntilAsync(() => factory.MockMail.SentMessages.Any(m => m.To == to)));
        var mail = factory.MockMail.SentMessages.Single(m => m.To == to);
        Assert.Equal("Tin nhắn mới từ Lan Nguyễn", mail.Subject);
        Assert.Contains("🔔 Bạn có tin nhắn mới từ Lan Nguyễn: 'Họp lúc 9h, xem [liên kết] nhé' trong AppInHouse", mail.TextBody);
        Assert.DoesNotContain("evil.example", mail.TextBody);
        Assert.DoesNotContain("evil.example", mail.HtmlBody);
        Assert.True(await UntilAsync(async () => await factory.WithDbAsync(db => db.NotificationLogs.AnyAsync(l => l.ProjectCode == project && l.TemplateCode == "ChatMessageOfflineEmail" && l.Status == "Sent"))));
    }

    [Fact]
    public async Task AMemberReachableByChat_GetsTheNoticeThere()
    {
        var project = NewProject();
        var chatId = "chat-" + Guid.NewGuid().ToString("N")[..8];
        await ActivateMockChatAsync(await AdminAsync(factory), project);
        var lan = User(factory, await ProjectKeyAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ChatSend), "lan", "Lan");
        var room = IdOf(await CreateDirectAsync(lan, "minh", chatId: chatId));

        await SendAsync(lan, room, "ping");

        Assert.True(await UntilAsync(() => MockChat(factory).SentMessages.Any(m => m.Recipient == chatId)));
        Assert.Contains("Bạn có tin nhắn mới từ Lan: 'ping' trong AppInHouse", MockChat(factory).SentMessages.Single(m => m.Recipient == chatId).Text);
    }

    [Fact]
    public async Task ABurstOfMessages_MakesOneEmail_AboutTheNewest()
    {
        var project = NewProject();
        var to = NewRecipient();
        await ActivateMockEmailAsync(await AdminAsync(factory), project);
        var lan = User(factory, await ProjectKeyAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ChatSend), "lan", "Lan");
        var room = IdOf(await CreateDirectAsync(lan, "minh", email: to));

        foreach (var text in new[] { "one", "two", "three" }) await SendAsync(lan, room, text);

        Assert.True(await UntilAsync(() => factory.MockMail.SentMessages.Any(m => m.To == to)));
        await Task.Delay(1500);   // time for the notices of the older messages to run (and to decide they have nothing to say)
        var mail = Assert.Single(factory.MockMail.SentMessages, m => m.To == to);
        Assert.Contains("'three'", mail.TextBody);
    }

    [Fact]
    public async Task AMemberWhoReadTheMessageInTheMeantime_GetsNoEmail()
    {
        var project = NewProject();
        var to = NewRecipient();
        await ActivateMockEmailAsync(await AdminAsync(factory), project);
        var key = await ProjectKeyAsync(factory, project, ApiScopes.EmailSend, ApiScopes.ChatSend);
        var lan = User(factory, key, "lan");
        var minh = User(factory, key, "minh");
        var room = IdOf(await CreateDirectAsync(lan, "minh", email: to));

        await SendAsync(lan, room, "ping");
        Assert.Equal(HttpStatusCode.OK, (await minh.PostAsync($"{Conversations}/{room}/read", null)).StatusCode);   // within the grace period

        await Task.Delay(3000);
        Assert.DoesNotContain(factory.MockMail.SentMessages, m => m.To == to);
        Assert.True(await factory.WithDbAsync(db => db.BackgroundJobs.AnyAsync(j => j.Type == "offline-notification" && j.PayloadJson.Contains(room.ToString()) && j.Status == JobStatus.Completed)));   // the job ran and decided to stay quiet
    }

    [Fact]
    public async Task AMemberWhoIsOnline_GetsNoEmail()
    {
        var project = NewProject();
        var to = NewRecipient();
        await ActivateMockEmailAsync(await AdminAsync(factory), project);
        var key = await ProjectKeyAsync(factory, project);
        var lan = User(factory, key, "lan");
        var room = IdOf(await CreateDirectAsync(lan, "minh", email: to));
        await using var minh = await HubProbe.ConnectAsync(factory, key, "minh");

        await SendAsync(lan, room, "ping");

        Assert.True(await HubProbe.UntilAsync(() => !minh.Messages.IsEmpty));
        await Task.Delay(2500);
        Assert.DoesNotContain(factory.MockMail.SentMessages, m => m.To == to);
        Assert.False(await factory.WithDbAsync(db => db.BackgroundJobs.AnyAsync(j => j.Type == "offline-notification" && j.PayloadJson.Contains(room.ToString()))));   // not even queued
    }

    [Fact]
    public async Task AMemberWithNoWayToBeReached_QueuesNothing()
    {
        var project = NewProject();
        var lan = User(factory, await ProjectKeyAsync(factory, project), "lan");
        var room = IdOf(await CreateDirectAsync(lan, "minh"));

        await SendAsync(lan, room, "ping");

        Assert.False(await factory.WithDbAsync(db => db.BackgroundJobs.AnyAsync(j => j.Type == "offline-notification" && j.PayloadJson.Contains(room.ToString()))));
    }

    // ------------------------------------------------------------------------------------------------ who is chatting (unit-sized checks of the resolver)

    private static HttpContext Context(string? apiKeyProject, string? user, string? name = null, string? requestedProject = null, string? sub = null, params string[] scopes)
    {
        var http = new DefaultHttpContext();
        var claims = new List<Claim>();
        if (apiKeyProject is not null) claims.Add(new Claim(ApiKeyDefaults.ProjectClaim, apiKeyProject));
        if (sub is not null) claims.Add(new Claim("sub", sub));
        claims.AddRange(scopes.Select(s => new Claim(ApiKeyDefaults.ScopeClaim, s)));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        if (user is not null) http.Request.Headers[ChatActorResolver.UserHeader] = user;
        if (name is not null) http.Request.Headers[ChatActorResolver.NameHeader] = name;
        if (requestedProject is not null) http.Request.Headers[ChatActorResolver.ProjectHeader] = requestedProject;
        return http;
    }

    [Fact]
    public void AKey_SpeaksForTheUserOfItsHeader_InItsOwnProject()
    {
        var ok = ChatActorResolver.TryResolve(Context("crm", "lan", Uri.EscapeDataString("Lan Nguyễn")), null, out var actor, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(("crm", "lan", "Lan Nguyễn", "crm:lan"), (actor.Project, actor.UserId, actor.Name, actor.Key));
    }

    [Fact]
    public void AKey_CannotNameAnotherProject_ThePlatformCan_AndAPlainBearerTokenNamesItsOwnSubject()
    {
        Assert.False(ChatActorResolver.TryResolve(Context("crm", "lan", requestedProject: "linguabridge"), null, out _, out var refusal));
        Assert.NotNull(refusal);

        Assert.True(ChatActorResolver.TryResolve(Context("default", "lan", requestedProject: "linguabridge"), null, out var platform, out _));
        Assert.Equal("linguabridge", platform.Project);

        // A bearer token: no project claim. The subject is the user; a header cannot make it someone else.
        var bearer = Context(null, "someone-else", sub: "user-42", requestedProject: "crm");
        Assert.True(ChatActorResolver.TryResolve(bearer, null, out var person, out _));
        Assert.Equal(("crm", "user-42"), (person.Project, person.UserId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two words")]
    public void AKeyWithoutAUsableUser_IsRefused(string? user)
    {
        Assert.False(ChatActorResolver.TryResolve(Context("crm", user), null, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ADisplayName_LosesControlCharacters_IsClipped_AndFallsBackToTheId()
    {
        Assert.Equal("lan", ChatActorResolver.CleanName("\r\n\t ", "lan"));
        Assert.Equal("LanX", ChatActorResolver.CleanName("Lan\r\nX\u0007", "lan"));
        Assert.Equal(100, ChatActorResolver.CleanName(new string('n', 500), "lan").Length);
        Assert.Equal("lan", ChatActorResolver.CleanName(null, "lan"));
    }

    [Fact]
    public void ATokenInTheAddress_BecomesABearerHeader_OnlyForTheHub_AndOnlyWithoutOtherCredentials()
    {
        static DefaultHttpContext Request(string path, string query, params (string Name, string Value)[] headers)
        {
            var http = new DefaultHttpContext();
            http.Request.Path = path;
            http.Request.QueryString = new QueryString(query);
            foreach (var (name, value) in headers) http.Request.Headers[name] = value;
            return http;
        }

        var hub = Request("/hubs/chat", "?access_token=abc");
        HubAuthentication.MoveTokenFromQuery(hub);
        Assert.Equal("Bearer abc", hub.Request.Headers.Authorization.ToString());
        Assert.Equal(string.Empty, hub.Request.QueryString.Value ?? string.Empty);   // the token is out of the address once it is in the header

        var negotiate = Request("/hubs/chat/negotiate", "?negotiateVersion=1&access_token=xyz");
        HubAuthentication.MoveTokenFromQuery(negotiate);
        Assert.Equal("Bearer xyz", negotiate.Request.Headers.Authorization.ToString());
        Assert.Equal("?negotiateVersion=1", negotiate.Request.QueryString.Value);   // the other parameters stay
        Assert.DoesNotContain("xyz", negotiate.Request.QueryString.Value);

        var elsewhere = Request("/api/v1/chat/conversations", "?access_token=abc");
        HubAuthentication.MoveTokenFromQuery(elsewhere);
        Assert.Equal(0, elsewhere.Request.Headers.Authorization.Count);

        var withKey = Request("/hubs/chat", "?access_token=abc", ("X-Api-Key", "aih_live_whatever"));
        HubAuthentication.MoveTokenFromQuery(withKey);
        Assert.Equal(0, withKey.Request.Headers.Authorization.Count);
        Assert.Equal("?access_token=abc", withKey.Request.QueryString.Value);   // untouched: nothing was moved

        var withHeader = Request("/hubs/chat", "?access_token=abc", ("Authorization", "Bearer real"));
        HubAuthentication.MoveTokenFromQuery(withHeader);
        Assert.Equal("Bearer real", withHeader.Request.Headers.Authorization.ToString());

        var noToken = Request("/hubs/chat", string.Empty);
        HubAuthentication.MoveTokenFromQuery(noToken);
        Assert.Equal(0, noToken.Request.Headers.Authorization.Count);
    }
}
