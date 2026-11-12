using System.Net;
using System.Text;
using System.Text.Json;
using AppInHouse.SharedServices.ClientSdk;

namespace AppInHouse.SharedServices.UnitTests;

/// <summary>The conversation calls of the SDK: the user headers, the addresses, the bodies, and what is made of the answers.</summary>
public class ConversationSdkTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? User, string? Name)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("X-Chat-User", out var user);
            request.Headers.TryGetValues("X-Chat-User-Name", out var name);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), user?.Single(), name?.Single()));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (SharedServicesClient Client, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new SharedServicesClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") }), handler);
    }

    private static readonly Guid Room = Guid.Parse("7f2d6a1e-0000-0000-0000-0000000000aa");

    private const string OneConversation = """
        {"id":"7f2d6a1e-0000-0000-0000-0000000000aa","projectCode":"crm","type":"Direct","title":null,"createdBy":"lan",
         "createdAt":"2026-11-12T08:00:00+00:00","lastMessageAt":"2026-11-12T08:00:00+00:00",
         "members":[{"userId":"lan","displayName":"Lan","role":"Member","joinedAt":"2026-11-12T08:00:00+00:00","isOnline":true}]}
        """;

    private const string OneMessage = """
        {"id":"7f2d6a1e-0000-0000-0000-0000000000bb","conversationId":"7f2d6a1e-0000-0000-0000-0000000000aa","senderId":"lan","senderName":"Lan",
         "type":"Text","content":"hi","attachmentFileId":null,"createdAt":"2026-11-12T08:01:00+00:00"}
        """;

    [Fact]
    public async Task Create_SendsTheUserInTheHeaders_AndTellsWhetherTheConversationWasNew()
    {
        var created = Make(_ => Json(HttpStatusCode.Created, OneConversation));
        var existing = Make(_ => Json(HttpStatusCode.OK, OneConversation));
        var request = new CreateConversationRequest("Direct", Members: [new ChatMemberRequest("minh", "Minh", "minh@example.com")]);

        var first = await created.Client.CreateConversationAsync(new ChatUser("lan", "Lan Nguyễn"), request);
        var again = await existing.Client.CreateConversationAsync(new ChatUser("lan"), request);

        Assert.True(first.Created);
        Assert.False(again.Created);
        Assert.Equal(Room, first.Conversation.Id);
        var sent = created.Handler.Requests.Single();
        Assert.Equal((HttpMethod.Post, "/api/v1/chat/conversations", "lan"), (sent.Method, sent.Url, sent.User));
        Assert.Equal(Uri.EscapeDataString("Lan Nguyễn"), sent.Name);   // a header cannot carry accents as they are: percent-encoded
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal("Direct", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("minh@example.com", body.RootElement.GetProperty("members")[0].GetProperty("email").GetString());
        Assert.Null(existing.Handler.Requests.Single().Name);
    }

    [Fact]
    public async Task APlatformKey_NamesTheProject_InTheAddress()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, "[]"));

        await client.GetConversationsAsync(new ChatUser("lan", ProjectCode: "linguabridge"));
        await client.GetConversationsAsync(new ChatUser("lan"));

        Assert.Equal("/api/v1/chat/conversations?projectCode=linguabridge", handler.Requests[0].Url);
        Assert.Equal("/api/v1/chat/conversations", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Messages_AreAskedForWithTheCursor_AndTheTimeIsSafeInAnAddress()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, """{"items":[],"hasMore":false,"nextBefore":null,"nextBeforeId":null}"""));
        var before = new DateTimeOffset(2026, 11, 12, 8, 30, 0, TimeSpan.Zero).AddTicks(1234560);
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var page = await client.GetConversationMessagesAsync(new ChatUser("lan"), Room, before, id, 50);
        await client.GetConversationMessagesAsync(new ChatUser("lan"), Room);

        Assert.False(page.HasMore);
        var url = handler.Requests[0].Url;
        Assert.StartsWith($"/api/v1/chat/conversations/{Room}/messages?before=2026-11-12T08%3A30%3A00.1234560%2B00%3A00&beforeId={id}&limit=50", url);   // the + of the offset is encoded
        Assert.Equal($"/api/v1/chat/conversations/{Room}/messages", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Send_PostsTheText_AndReadsTheMessage()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.Created, OneMessage));

        var message = await client.SendConversationMessageAsync(new ChatUser("lan"), Room, "hi");

        Assert.Equal(("hi", "Lan", "Text"), (message.Content, message.SenderName, message.Type));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 8, 1, 0, TimeSpan.Zero), message.CreatedAt);
        var sent = handler.Requests.Single();
        Assert.Equal($"/api/v1/chat/conversations/{Room}/messages", sent.Url);
        Assert.Contains("\"content\":\"hi\"", sent.Body);
    }

    [Fact]
    public async Task MarkRead_ReturnsTheTimeTheServerRecorded()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, """{"readAt":"2026-11-12T09:00:00.5+00:00"}"""));

        var readAt = await client.MarkConversationReadAsync(new ChatUser("lan"), Room);

        Assert.Equal(new DateTimeOffset(2026, 11, 12, 9, 0, 0, 500, TimeSpan.Zero), readAt);
        Assert.Equal((HttpMethod.Post, $"/api/v1/chat/conversations/{Room}/read"), (handler.Requests[0].Method, handler.Requests[0].Url));
    }

    [Fact]
    public async Task Rename_AddMembers_AndRemoveMember_UseTheirOwnVerbsAndAddresses()
    {
        var (client, handler) = Make(request => request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.OK, OneConversation));
        var user = new ChatUser("lan");

        await client.RenameConversationAsync(user, Room, "New name");
        await client.AddConversationMembersAsync(user, Room, [new ChatMemberRequest("bao")]);
        await client.RemoveConversationMemberAsync(user, Room, "minh@work");

        Assert.Equal((HttpMethod.Put, $"/api/v1/chat/conversations/{Room}"), (handler.Requests[0].Method, handler.Requests[0].Url));
        Assert.Contains("\"title\":\"New name\"", handler.Requests[0].Body);
        Assert.Equal((HttpMethod.Post, $"/api/v1/chat/conversations/{Room}/members"), (handler.Requests[1].Method, handler.Requests[1].Url));
        Assert.Contains("\"userId\":\"bao\"", handler.Requests[1].Body);
        Assert.Equal((HttpMethod.Delete, $"/api/v1/chat/conversations/{Room}/members/minh%40work"), (handler.Requests[2].Method, handler.Requests[2].Url));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "You are not a member of this conversation.")]
    [InlineData(HttpStatusCode.NotFound, "There is no such conversation.")]
    [InlineData(HttpStatusCode.BadRequest, "A message needs some text or a file.")]
    public async Task ARefusal_ThrowsTheApiExceptionWithTheStatusAndTheReason(HttpStatusCode status, string detail)
    {
        var (client, _) = Make(_ => Json(status, $$"""{"title":"refused","status":{{(int)status}},"detail":"{{detail}}"}"""));

        var thrown = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.GetConversationMessagesAsync(new ChatUser("eve"), Room));
        var sent = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.SendConversationMessageAsync(new ChatUser("eve"), Room, "x"));
        var read = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.MarkConversationReadAsync(new ChatUser("eve"), Room));
        var left = await Assert.ThrowsAsync<SharedServicesApiException>(() => client.RemoveConversationMemberAsync(new ChatUser("eve"), Room, "lan"));

        Assert.All(new[] { thrown, sent, read, left }, ex => Assert.Equal(((int)status, detail), (ex.StatusCode, ex.Message)));
    }

    [Fact]
    public async Task TheMonitor_IsAskedForWithOrWithoutAProject()
    {
        const string body = """{"projectCode":"crm","conversations":2,"directConversations":1,"groupConversations":1,"messagesToday":5,"onlineUsers":1,"connections":2,"onlineUserIds":["lan"],"recent":[{"id":"7f2d6a1e-0000-0000-0000-0000000000aa","projectCode":"crm","type":"Group","title":"Team","memberCount":3,"messageCount":4,"lastMessageAt":"2026-11-12T08:00:00+00:00"}]}""";
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, body));

        var monitor = await client.GetChatMonitorAsync("crm");
        await client.GetChatMonitorAsync();

        Assert.Equal((2, 5, 1, 2), (monitor.Conversations, monitor.MessagesToday, monitor.OnlineUsers, monitor.Connections));
        Assert.Equal(["lan"], monitor.OnlineUserIds);
        Assert.Equal(("Team", 3, 4), (monitor.Recent[0].Title, monitor.Recent[0].MemberCount, monitor.Recent[0].MessageCount));
        Assert.Equal("/api/v1/chat/monitor?projectCode=crm", handler.Requests[0].Url);
        Assert.Equal("/api/v1/chat/monitor", handler.Requests[1].Url);
    }

    // ------------------------------------------------------------------------------------------------ the realtime check

    [Fact]
    public async Task TheRealtimeCheck_NegotiatesWithTheHub_AndReportsTheTransports()
    {
        var (client, handler) = Make(_ => Json(HttpStatusCode.OK, """{"negotiateVersion":1,"connectionId":"x","availableTransports":[{"transport":"WebSockets","transferFormats":["Text","Binary"]},{"transport":"LongPolling","transferFormats":["Text"]}]}"""));

        var result = await client.CheckRealtimeAsync();

        Assert.True(result.Reachable);
        Assert.Equal(["WebSockets", "LongPolling"], result.Transports);
        Assert.Contains("WebSockets", result.Message);
        Assert.Equal(200, result.StatusCode);
        var sent = handler.Requests.Single();
        Assert.Equal((HttpMethod.Post, "/hubs/chat/negotiate?negotiateVersion=1", "portal-probe"), (sent.Method, sent.Url, sent.User));
    }

    [Fact]
    public async Task TheRealtimeCheck_WithoutWebSockets_SaysSo()
    {
        var (client, _) = Make(_ => Json(HttpStatusCode.OK, """{"availableTransports":[{"transport":"LongPolling"}]}"""));

        var result = await client.CheckRealtimeAsync();

        Assert.True(result.Reachable);
        Assert.Contains("WebSockets are not offered", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "chat:converse")]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.BadGateway, "502")]
    public async Task TheRealtimeCheck_ReportsARefusal_NeverThrows(HttpStatusCode status, string mention)
    {
        var (client, _) = Make(_ => Json(status, "{}"));

        var result = await client.CheckRealtimeAsync();

        Assert.False(result.Reachable);
        Assert.Contains(mention, result.Message);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Empty(result.Transports);
    }

    [Fact]
    public async Task TheRealtimeCheck_ReportsAHubThatCannotBeReached_AndAnAnswerThatIsNotJson()
    {
        var down = new SharedServicesClient(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://api.test/") });
        var (garbled, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy page</html>") });

        var unreachable = await down.CheckRealtimeAsync();
        var notJson = await garbled.CheckRealtimeAsync();

        Assert.False(unreachable.Reachable);
        Assert.Null(unreachable.StatusCode);
        Assert.False(notJson.Reachable);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
