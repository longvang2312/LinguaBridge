using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using static AppInHouse.SharedServices.IntegrationTests.Support.ChatApi;
using static AppInHouse.SharedServices.IntegrationTests.Support.PlatformApi;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>The realtime hub, over real WebSockets (the test server's socket client), with the real services and database behind it.</summary>
public class ChatHubTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // ------------------------------------------------------------------------------------------------ connecting

    [Fact]
    public async Task TheHub_NeedsAKey_TheChatScope_AndAUser()
    {
        var project = NewProject();
        var withScope = await ProjectKeyAsync(factory, project);
        var withoutScope = (await factory.CreateKeyForProjectAsync(project, null, ApiScopes.ChatSend, ApiScopes.ConfigRead)).PlainKey;

        async Task<HttpStatusCode> NegotiateAsync(string? key, string? user)
        {
            var client = factory.ClientWithKey(key);
            if (user is not null) client.DefaultRequestHeaders.Add("X-Chat-User", user);
            return (await client.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(null, "lan"));
        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync("aih_live_not-a-real-key-at-all-0000000000000", "lan"));
        Assert.Equal(HttpStatusCode.Forbidden, await NegotiateAsync(withoutScope, "lan"));
        Assert.Equal(HttpStatusCode.BadRequest, await NegotiateAsync(withScope, null));
        Assert.Equal(HttpStatusCode.BadRequest, await NegotiateAsync(withScope, "bad id"));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(withScope, "lan"));
    }

    [Fact]
    public async Task ANegotiation_OffersWebSockets()
    {
        var key = await ProjectKeyAsync(factory, NewProject());
        var client = User(factory, key, "lan");

        var response = await client.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null);
        var transports = (await ReadAsync(response)).GetProperty("availableTransports").EnumerateArray().Select(t => t.GetProperty("transport").GetString()).ToList();

        Assert.Contains("WebSockets", transports);
    }

    [Fact]
    public async Task AConnectionWithoutACredential_IsNeverEstablished()
    {
        var key = await ProjectKeyAsync(factory, NewProject());

        await Assert.ThrowsAnyAsync<Exception>(() => HubProbe.ConnectAsync(factory, null, "lan"));
        await Assert.ThrowsAnyAsync<Exception>(() => HubProbe.ConnectAsync(factory, key, null));
    }

    // ------------------------------------------------------------------------------------------------ messages

    [Fact]
    public async Task AMessageSentOnTheHub_ReachesTheOtherMemberAndTheSenderOtherTab_AndIsSaved()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var room = IdOf(await CreateDirectAsync(User(factory, key, "lan"), "minh"));
        await using var lan = await HubProbe.ConnectAsync(factory, key, "lan", displayName: "Lan Nguyễn");
        await using var lanPhone = await HubProbe.ConnectAsync(factory, key, "lan");
        await using var minh = await HubProbe.ConnectAsync(factory, key, "minh");

        var returned = await lan.Connection.InvokeAsync<Application.Conversations.ChatMessageDto>("SendMessage", room, "Chào Minh", (Guid?)null);

        Assert.True(await HubProbe.UntilAsync(() => minh.Messages.Count == 1 && lanPhone.Messages.Count == 1 && lan.Messages.Count == 1));
        var received = Assert.Single(minh.Messages);
        Assert.Equal(("lan", "Lan Nguyễn", "Text", "Chào Minh", room), (received.SenderId, received.SenderName, received.Type, received.Content, received.ConversationId));
        Assert.Equal(returned.Id, received.Id);
        Assert.Equal(received.Id, Assert.Single(lanPhone.Messages).Id);
        var saved = await factory.WithDbAsync(db => db.ChatMessages.SingleAsync(m => m.ConversationId == room));
        Assert.Equal((received.Id, "Chào Minh"), (saved.Id, saved.Content));
    }

    [Fact]
    public async Task AMessageSentByRest_IsPushedToTheConnectedMembers()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lanClient = User(factory, key, "lan");
        var room = IdOf(await CreateDirectAsync(lanClient, "minh"));
        await using var minh = await HubProbe.ConnectAsync(factory, key, "minh");

        await SendAsync(lanClient, room, "from the server of the project");

        Assert.True(await HubProbe.UntilAsync(() => minh.Messages.Count == 1));
        Assert.Equal("from the server of the project", minh.Messages.Single().Content);
    }

    [Fact]
    public async Task APersonOutsideTheRoom_CannotSendOnTheHub_AndNeverHearsTheRoom()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lanClient = User(factory, key, "lan");
        var room = IdOf(await CreateDirectAsync(lanClient, "minh"));
        await using var eve = await HubProbe.ConnectAsync(factory, key, "eve");
        await using var minh = await HubProbe.ConnectAsync(factory, key, "minh");

        var denied = await Assert.ThrowsAsync<HubException>(() => eve.Connection.InvokeAsync<Application.Conversations.ChatMessageDto>("SendMessage", room, "let me in", (Guid?)null));
        await SendAsync(lanClient, room, "private talk");
        Assert.True(await HubProbe.UntilAsync(() => minh.Messages.Count == 1));
        await HubProbe.QuietAsync();

        Assert.Contains("Forbidden", denied.Message);
        Assert.Empty(eve.Messages);
        Assert.Equal(1, await factory.WithDbAsync(db => db.ChatMessages.CountAsync(m => m.ConversationId == room)));
        await Assert.ThrowsAsync<HubException>(() => eve.Connection.InvokeAsync("MarkAsRead", room));
        await Assert.ThrowsAsync<HubException>(() => eve.Connection.InvokeAsync("Typing", room, true));
    }

    [Fact]
    public async Task TheSameUserIdInAnotherProject_NeverHearsThisProject()
    {
        var a = NewProject();
        var b = NewProject();
        var keyA = await ProjectKeyAsync(factory, a);
        var keyB = await ProjectKeyAsync(factory, b);
        var lanClient = User(factory, keyA, "lan");
        var room = IdOf(await CreateDirectAsync(lanClient, "minh"));
        await using var minhA = await HubProbe.ConnectAsync(factory, keyA, "minh");
        await using var minhB = await HubProbe.ConnectAsync(factory, keyB, "minh");   // another person with the same id

        await SendAsync(lanClient, room, "for project A only");

        Assert.True(await HubProbe.UntilAsync(() => minhA.Messages.Count == 1));
        await HubProbe.QuietAsync();
        Assert.Empty(minhB.Messages);
        await Assert.ThrowsAsync<HubException>(() => minhB.Connection.InvokeAsync<Application.Conversations.ChatMessageDto>("SendMessage", room, "cross-project", (Guid?)null));
    }

    [Fact]
    public async Task BadHubCalls_AreRefusedWithAPlainMessage()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var room = IdOf(await CreateDirectAsync(User(factory, key, "lan"), "minh"));
        await using var lan = await HubProbe.ConnectAsync(factory, key, "lan");

        var empty = await Assert.ThrowsAsync<HubException>(() => lan.Connection.InvokeAsync<Application.Conversations.ChatMessageDto>("SendMessage", room, "  ", (Guid?)null));
        var unknown = await Assert.ThrowsAsync<HubException>(() => lan.Connection.InvokeAsync<Application.Conversations.ChatMessageDto>("SendMessage", Guid.NewGuid(), "hi", (Guid?)null));

        Assert.Contains("Invalid", empty.Message);
        Assert.Contains("NotFound", unknown.Message);
        Assert.Equal(0, await factory.WithDbAsync(db => db.ChatMessages.CountAsync(m => m.ConversationId == room)));
    }

    // ------------------------------------------------------------------------------------------------ read, typing

    [Fact]
    public async Task MarkingRead_IsPushedToTheMembers_AndSaved()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lanClient = User(factory, key, "lan");
        var room = IdOf(await CreateDirectAsync(lanClient, "minh"));
        await SendAsync(lanClient, room, "ping");
        await using var lan = await HubProbe.ConnectAsync(factory, key, "lan");
        await using var minh = await HubProbe.ConnectAsync(factory, key, "minh");

        await minh.Connection.InvokeAsync("MarkAsRead", room);

        Assert.True(await HubProbe.UntilAsync(() => lan.Reads.Count == 1 && minh.Reads.Count == 1));
        var read = lan.Reads.Single();
        Assert.Equal((room, "minh"), (read.Conversation, read.User));
        var saved = await factory.WithDbAsync(db => db.ConversationMembers.SingleAsync(m => m.ConversationId == room && m.UserId == "minh"));
        Assert.Equal(saved.LastReadAt, read.At);
        Assert.Equal(0, (await ReadAsync(await User(factory, key, "minh").GetAsync(Conversations))).EnumerateArray().Single().GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task Typing_IsShownToTheOthersOnly()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var room = IdOf(await CreateGroupAsync(User(factory, key, "lan"), "Team", "minh", "an"));
        await using var lan = await HubProbe.ConnectAsync(factory, key, "lan");
        await using var minh = await HubProbe.ConnectAsync(factory, key, "minh");
        await using var an = await HubProbe.ConnectAsync(factory, key, "an");
        await using var eve = await HubProbe.ConnectAsync(factory, key, "eve");

        await lan.Connection.InvokeAsync("Typing", room, true);
        await lan.Connection.InvokeAsync("Typing", room, false);

        Assert.True(await HubProbe.UntilAsync(() => minh.Typing.Count == 2 && an.Typing.Count == 2));
        Assert.Equal([(room, "lan", true), (room, "lan", false)], minh.Typing.ToArray());
        await HubProbe.QuietAsync();
        Assert.Empty(lan.Typing);
        Assert.Empty(eve.Typing);
    }

    // ------------------------------------------------------------------------------------------------ presence

    [Fact]
    public async Task Presence_ChangesWhenTheFirstTabOpensAndTheLastTabCloses_NotInBetween()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        await CreateDirectAsync(User(factory, key, "lan"), "minh");
        await using var lan = await HubProbe.ConnectAsync(factory, key, "lan");
        Assert.Empty(await lan.Connection.InvokeAsync<List<string>>("GetOnlinePeers"));

        var tab1 = await HubProbe.ConnectAsync(factory, key, "minh");
        Assert.True(await HubProbe.UntilAsync(() => lan.Presence.Count == 1));
        Assert.Equal(("minh", true), lan.Presence.Single());
        Assert.Equal(["minh"], await lan.Connection.InvokeAsync<List<string>>("GetOnlinePeers"));

        var tab2 = await HubProbe.ConnectAsync(factory, key, "minh");
        await tab1.DisposeAsync();
        await HubProbe.QuietAsync();
        Assert.Single(lan.Presence);   // still online through the second tab

        await tab2.DisposeAsync();
        Assert.True(await HubProbe.UntilAsync(() => lan.Presence.Count == 2));
        Assert.Equal(("minh", false), lan.Presence.ToArray()[1]);
        Assert.Empty(await lan.Connection.InvokeAsync<List<string>>("GetOnlinePeers"));
    }

    [Fact]
    public async Task Presence_IsToldOnlyToPeers_NeverToStrangers()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        await CreateDirectAsync(User(factory, key, "lan"), "minh");
        await using var stranger = await HubProbe.ConnectAsync(factory, key, "stranger");
        await using var lan = await HubProbe.ConnectAsync(factory, key, "lan");

        await using (var minh = await HubProbe.ConnectAsync(factory, key, "minh"))
            Assert.True(await HubProbe.UntilAsync(() => lan.Presence.Count == 1));

        await HubProbe.QuietAsync();
        Assert.Empty(stranger.Presence);
    }

    [Fact]
    public async Task TheMonitor_CountsTheConnections_AndForgetsThemWhenTheyClose()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project, ApiScopes.ConfigRead);
        var monitor = factory.ClientWithKey(key);
        var lan = await HubProbe.ConnectAsync(factory, key, "lan");
        var lanPhone = await HubProbe.ConnectAsync(factory, key, "lan");
        var minh = await HubProbe.ConnectAsync(factory, key, "minh");

        // The server registers a connection just after the handshake answer, so give it a moment instead of asking at once.
        Assert.True(await UntilAsync(async () => (await ReadAsync(await monitor.GetAsync("/api/v1/chat/monitor"))).GetProperty("connections").GetInt32() == 3));
        var open = await ReadAsync(await monitor.GetAsync("/api/v1/chat/monitor"));
        Assert.Equal((2, 3), (open.GetProperty("onlineUsers").GetInt32(), open.GetProperty("connections").GetInt32()));
        Assert.Equal(["lan", "minh"], open.GetProperty("onlineUserIds").EnumerateArray().Select(u => u.GetString()).Order());

        await lan.DisposeAsync();
        await lanPhone.DisposeAsync();
        await minh.DisposeAsync();
        Assert.True(await UntilAsync(async () => (await ReadAsync(await monitor.GetAsync("/api/v1/chat/monitor"))).GetProperty("connections").GetInt32() == 0));
        Assert.Equal(0, (await ReadAsync(await monitor.GetAsync("/api/v1/chat/monitor"))).GetProperty("onlineUsers").GetInt32());
    }

    // ------------------------------------------------------------------------------------------------ leaving

    [Fact]
    public async Task AMemberWhoIsRemoved_IsToldAboutIt_AndHearsNothingMore()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lanClient = User(factory, key, "lan");
        var room = IdOf(await CreateGroupAsync(lanClient, "Team", "minh", "an"));
        await using var minh = await HubProbe.ConnectAsync(factory, key, "minh");
        await using var an = await HubProbe.ConnectAsync(factory, key, "an");
        await SendAsync(lanClient, room, "before");
        Assert.True(await HubProbe.UntilAsync(() => minh.Messages.Count == 1 && an.Messages.Count == 1));

        Assert.Equal(HttpStatusCode.NoContent, (await lanClient.DeleteAsync($"{Conversations}/{room}/members/minh")).StatusCode);

        Assert.True(await HubProbe.UntilAsync(() => !minh.Changes.IsEmpty && !an.Changes.IsEmpty));   // everyone, the removed one included, learns the room changed
        var announced = minh.Messages.Count;
        await SendAsync(lanClient, room, "after");
        Assert.True(await HubProbe.UntilAsync(() => an.Messages.Any(m => m.Content == "after")));
        await HubProbe.QuietAsync();
        Assert.DoesNotContain(minh.Messages, m => m.Content == "after");
        Assert.True(announced >= 1);
        await Assert.ThrowsAsync<HubException>(() => minh.Connection.InvokeAsync<Application.Conversations.ChatMessageDto>("SendMessage", room, "still here?", (Guid?)null));
    }

    [Fact]
    public async Task AnAddedMember_StartsHearingTheRoomWithoutReconnecting()
    {
        var project = NewProject();
        var key = await ProjectKeyAsync(factory, project);
        var lanClient = User(factory, key, "lan");
        var room = IdOf(await CreateGroupAsync(lanClient, "Team", "minh"));
        await using var bao = await HubProbe.ConnectAsync(factory, key, "bao");   // connected before being a member

        var added = await lanClient.PostAsJsonAsync($"{Conversations}/{room}/members", new { members = new[] { new { userId = "bao" } } });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        await SendAsync(lanClient, room, "welcome");

        Assert.True(await HubProbe.UntilAsync(() => bao.Messages.Any(m => m.Content == "welcome") && !bao.Changes.IsEmpty));
    }
}
