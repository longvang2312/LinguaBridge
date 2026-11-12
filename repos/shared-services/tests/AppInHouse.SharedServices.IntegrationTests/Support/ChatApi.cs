using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace AppInHouse.SharedServices.IntegrationTests.Support;

/// <summary>What the chat tests share: a key per project, a client per user, and a probe that listens on the realtime hub.</summary>
internal static class ChatApi
{
    public const string Conversations = "/api/v1/chat/conversations";

    public static string NewUser(string name = "u") => name + "-" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>A key of the project that may use conversations (and any extra scopes).</summary>
    public static async Task<string> ProjectKeyAsync(ApiFactory factory, string project, params string[] extraScopes) =>
        (await factory.CreateKeyForProjectAsync(project, null, [ApiScopes.ChatConverse, .. extraScopes])).PlainKey;

    /// <summary>A client that speaks for one user of the key's project (the server of a project names its user in a header).</summary>
    public static HttpClient User(ApiFactory factory, string key, string user, string? displayName = null)
    {
        var client = factory.ClientWithKey(key);
        client.DefaultRequestHeaders.Add("X-Chat-User", user);
        if (displayName is not null) client.DefaultRequestHeaders.Add("X-Chat-User-Name", Uri.EscapeDataString(displayName));
        return client;
    }

    public static async Task<JsonElement> CreateDirectAsync(HttpClient client, string other, string? email = null, string? chatId = null, string? otherName = null)
    {
        var response = await client.PostAsJsonAsync(Conversations, new { type = "Direct", members = new[] { new { userId = other, displayName = otherName, email, chatId } } });
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await PlatformApi.ReadAsync(response);
    }

    public static async Task<JsonElement> CreateGroupAsync(HttpClient client, string title, params string[] members)
    {
        var response = await client.PostAsJsonAsync(Conversations, new { type = "Group", title, members = members.Select(m => new { userId = m }) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await PlatformApi.ReadAsync(response);
    }

    public static async Task<JsonElement> SendAsync(HttpClient client, Guid conversation, string content)
    {
        var response = await client.PostAsJsonAsync($"{Conversations}/{conversation}/messages", new { content });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await PlatformApi.ReadAsync(response);
    }

    public static Guid IdOf(JsonElement conversation) => conversation.GetProperty("id").GetGuid();
}

/// <summary>A user connected to the hub. Everything the server pushes to the connection is kept for the test to look at.</summary>
internal sealed class HubProbe : IAsyncDisposable
{
    private HubProbe(HubConnection connection) => Connection = connection;

    public HubConnection Connection { get; }
    public ConcurrentQueue<ChatMessageDto> Messages { get; } = new();
    public ConcurrentQueue<(string User, bool Online)> Presence { get; } = new();
    public ConcurrentQueue<(Guid Conversation, string User, DateTimeOffset At)> Reads { get; } = new();
    public ConcurrentQueue<(Guid Conversation, string User, bool Typing)> Typing { get; } = new();
    public ConcurrentQueue<Guid> Changes { get; } = new();

    /// <summary>Connects over WebSockets (the test server's own socket client) with the credential and user given.</summary>
    public static async Task<HubProbe> ConnectAsync(ApiFactory factory, string? key, string? user, string? project = null, string? displayName = null)
    {
        void Headers(IDictionary<string, string> headers)
        {
            if (key is not null) headers["X-Api-Key"] = key;
            if (user is not null) headers["X-Chat-User"] = user;
            if (project is not null) headers["X-Chat-Project"] = project;
            if (displayName is not null) headers["X-Chat-User-Name"] = Uri.EscapeDataString(displayName);
        }

        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                Headers(options.Headers);
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        var headers = new Dictionary<string, string>();
                        Headers(headers);
                        foreach (var (name, value) in headers) request.Headers[name] = value;
                    };
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();

        var probe = new HubProbe(connection);
        connection.On<ChatMessageDto>("ReceiveMessage", message => probe.Messages.Enqueue(message));
        connection.On<string, bool>("UserPresenceChanged", (who, online) => probe.Presence.Enqueue((who, online)));
        connection.On<Guid, string, DateTimeOffset>("MessageRead", (conversation, who, at) => probe.Reads.Enqueue((conversation, who, at)));
        connection.On<Guid, string, bool>("UserTyping", (conversation, who, typing) => probe.Typing.Enqueue((conversation, who, typing)));
        connection.On<Guid>("ConversationChanged", conversation => probe.Changes.Enqueue(conversation));
        await connection.StartAsync();
        return probe;
    }

    /// <summary>Waits up to a few seconds for something the server was told to push.</summary>
    public static Task<bool> UntilAsync(Func<bool> condition) => PlatformApi.UntilAsync(condition, seconds: 10);

    /// <summary>Gives a push that should NOT happen time to arrive (the pushes of the server are sent before its answer returns).</summary>
    public static Task QuietAsync() => Task.Delay(400);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Connection.DisposeAsync();
        }
        catch (Exception)
        {
            // Already closed (a test that dropped it on purpose).
        }
    }
}
