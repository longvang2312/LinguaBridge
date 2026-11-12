using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.IntegrationTests.Support;
using static AppInHouse.SharedServices.IntegrationTests.Support.ChatApi;

namespace AppInHouse.SharedServices.IntegrationTests;

/// <summary>The SDK against the real API: what a project's server really gets when it uses the typed methods.</summary>
public class ConversationSdkEndToEndTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<SharedServicesClient> ClientAsync(string project) =>
        new(factory.ClientWithKey(await ProjectKeyAsync(factory, project)));

    [Fact]
    public async Task AProjectServer_RunsAWholeConversation_ThroughTheSdk()
    {
        var sdk = await ClientAsync(PlatformApi.NewProject());
        var lan = new ChatUser("lan", "Lan Nguyễn");
        var minh = new ChatUser("minh", "Minh");

        var made = await sdk.CreateConversationAsync(lan, new CreateConversationRequest("Direct", Members: [new ChatMemberRequest("minh", "Minh")]));
        var again = await sdk.CreateConversationAsync(minh, new CreateConversationRequest("Direct", Members: [new ChatMemberRequest("lan")]));
        Assert.True(made.Created);
        Assert.False(again.Created);
        Assert.Equal(made.Conversation.Id, again.Conversation.Id);
        var room = made.Conversation.Id;

        for (var i = 0; i < 35; i++) await sdk.SendConversationMessageAsync(lan, room, $"m{i:D2}");
        var sent = await sdk.SendConversationMessageAsync(lan, room, "Chào Minh 👋");
        Assert.Equal(("Lan Nguyễn", "Text"), (sent.SenderName, sent.Type));

        var summary = Assert.Single(await sdk.GetConversationsAsync(minh));
        Assert.Equal((36, "Chào Minh 👋"), (summary.UnreadCount, summary.LastMessage!.Content));

        // Walk the history with the cursor the API handed out, exactly as a caller would.
        var seen = new List<string>();
        var page = await sdk.GetConversationMessagesAsync(minh, room);
        Assert.Equal((30, true), (page.Items.Count, page.HasMore));
        while (true)
        {
            seen.AddRange(page.Items.Select(m => m.Content));
            if (!page.HasMore) break;
            page = await sdk.GetConversationMessagesAsync(minh, room, page.NextBefore, page.NextBeforeId);
        }

        Assert.Equal(36, seen.Count);
        Assert.Equal(36, seen.Distinct().Count());
        Assert.Equal("Chào Minh 👋", seen[0]);
        Assert.Equal("m00", seen[^1]);

        var readAt = await sdk.MarkConversationReadAsync(minh, room);
        Assert.True(readAt >= sent.CreatedAt);
        Assert.Equal(0, Assert.Single(await sdk.GetConversationsAsync(minh)).UnreadCount);
    }

    [Fact]
    public async Task AGroup_IsManagedThroughTheSdk_AndAnOutsiderIsRefusedWithA403()
    {
        var sdk = await ClientAsync(PlatformApi.NewProject());
        var lan = new ChatUser("lan");
        var minh = new ChatUser("minh");
        var eve = new ChatUser("eve");

        var group = (await sdk.CreateConversationAsync(lan, new CreateConversationRequest("Group", "Team", [new ChatMemberRequest("minh")]))).Conversation;
        Assert.Equal(("Team", 2), (group.Title, group.Members.Count));

        var added = await sdk.AddConversationMembersAsync(lan, group.Id, [new ChatMemberRequest("an", "An")]);
        Assert.Equal(3, added.Members.Count);
        Assert.Equal("Renamed", (await sdk.RenameConversationAsync(lan, group.Id, "Renamed")).Title);

        var memberTries = await Assert.ThrowsAsync<SharedServicesApiException>(() => sdk.AddConversationMembersAsync(minh, group.Id, [new ChatMemberRequest("bao")]));
        var outsider = await Assert.ThrowsAsync<SharedServicesApiException>(() => sdk.GetConversationMessagesAsync(eve, group.Id));
        var outsiderSend = await Assert.ThrowsAsync<SharedServicesApiException>(() => sdk.SendConversationMessageAsync(eve, group.Id, "hi"));
        var empty = await Assert.ThrowsAsync<SharedServicesApiException>(() => sdk.SendConversationMessageAsync(lan, group.Id, "  "));
        var unknown = await Assert.ThrowsAsync<SharedServicesApiException>(() => sdk.GetConversationMessagesAsync(lan, Guid.NewGuid()));

        Assert.Equal([403, 403, 403, 400, 404], new[] { memberTries, outsider, outsiderSend, empty, unknown }.Select(e => e.StatusCode));
        Assert.Contains("not a member", outsider.Message);

        await sdk.RemoveConversationMemberAsync(minh, group.Id, "minh");   // leaves
        Assert.Equal(2, (await sdk.GetConversationsAsync(lan)).Single().Members.Count);
        var gone = await Assert.ThrowsAsync<SharedServicesApiException>(() => sdk.GetConversationMessagesAsync(minh, group.Id));
        Assert.Equal(403, gone.StatusCode);
    }

    [Fact]
    public async Task TheSdk_MonitorAndRealtimeCheck_WorkAgainstTheRealApi()
    {
        var project = PlatformApi.NewProject();
        var key = await ProjectKeyAsync(factory, project, Domain.ApiScopes.ConfigRead);
        var sdk = new SharedServicesClient(factory.ClientWithKey(key));
        await sdk.CreateConversationAsync(new ChatUser("lan"), new CreateConversationRequest("Direct", Members: [new ChatMemberRequest("minh")]));

        var monitor = await sdk.GetChatMonitorAsync();
        var check = await sdk.CheckRealtimeAsync();

        Assert.Equal((project, 1, 1), (monitor.ProjectCode, monitor.Conversations, monitor.DirectConversations));
        Assert.True(check.Reachable, check.Message);
        Assert.Contains("WebSockets", check.Transports);
    }
}
