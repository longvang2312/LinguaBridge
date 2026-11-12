using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AppInHouse.SharedServices.Api.Chat;

/// <summary>
/// The realtime side of the chat, at <c>/hubs/chat</c>. A connection is one tab or device of one user (see <see cref="ChatActorResolver"/> for
/// who that is); a user can have several. Clients call <c>SendMessage</c>, <c>MarkAsRead</c>, <c>Typing</c> and <c>GetOnlinePeers</c>, and receive
/// <c>ReceiveMessage</c>, <c>MessageRead</c>, <c>UserTyping</c>, <c>UserPresenceChanged</c> and <c>ConversationChanged</c>. Every rule (membership,
/// roles, project isolation) is the conversation service's, the same one the REST endpoints use: the hub only carries the call.
/// </summary>
[Authorize(Policy = Policies.ChatConverse)]
public sealed class ChatHub(IConversationService conversations, IUserPresenceTracker presence) : Hub
{
    public const string Path = "/hubs/chat";
    private const string ActorKey = "chat.actor";

    private ChatActor Actor => Context.Items[ActorKey] as ChatActor ?? throw new HubException("Not signed in to the chat.");

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (http is null || !ChatActorResolver.TryResolve(http, null, out var actor, out _))
        {
            Context.Abort();
            return;
        }

        Context.Items[ActorKey] = actor;
        var change = presence.Connected(actor.Project, actor.UserId, Context.ConnectionId);
        await base.OnConnectedAsync();
        if (change.Changed) await conversations.AnnouncePresenceAsync(actor.Project, actor.UserId, true, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var change = presence.Disconnected(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);

        // The connection is already gone, so its token is cancelled: tell the others regardless.
        if (change.Changed && !change.IsOnline) await conversations.AnnouncePresenceAsync(change.Project, change.UserId, false, CancellationToken.None);
    }

    public Task<ChatMessageDto> SendMessage(Guid conversationId, string content, Guid? attachmentFileId = null) =>
        Guard(() => conversations.SendAsync(Actor, conversationId, content, attachmentFileId, Context.ConnectionAborted));

    public Task MarkAsRead(Guid conversationId) =>
        Guard(async () =>
        {
            await conversations.MarkReadAsync(Actor, conversationId, Context.ConnectionAborted);
            return true;
        });

    public Task Typing(Guid conversationId, bool isTyping) =>
        Guard(async () =>
        {
            await conversations.AnnounceTypingAsync(Actor, conversationId, isTyping, Context.ConnectionAborted);
            return true;
        });

    /// <summary>The users who share a conversation with the caller and are online now (the start of the picture; changes arrive as <c>UserPresenceChanged</c>).</summary>
    public Task<IReadOnlyList<string>> GetOnlinePeers() =>
        Guard(() => conversations.GetOnlinePeersAsync(Actor, Context.ConnectionAborted));

    /// <summary>
    /// A refusal reaches the caller as a <see cref="HubException"/> with a plain message (SignalR hides the text of any other exception).
    /// </summary>
    private static async Task<T> Guard<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ConversationAccessDeniedException ex)
        {
            throw new HubException($"Forbidden: {ex.Message}");
        }
        catch (NotFoundException ex)
        {
            throw new HubException($"NotFound: {ex.Message}");
        }
        catch (ValidationException ex)
        {
            throw new HubException("Invalid: " + string.Join(" ", ex.Errors.SelectMany(e => e.Value)));
        }
        catch (DomainException ex)
        {
            throw new HubException($"Invalid: {ex.Message}");
        }
    }
}

/// <summary>Pushes through the hub. Each user is addressed by the "project:user" identity, so users of two projects with the same id never hear each other.</summary>
public sealed class SignalRChatRealtime(IHubContext<ChatHub> hub) : IChatRealtime
{
    private IClientProxy To(string project, IReadOnlyCollection<string> userIds) =>
        hub.Clients.Users(userIds.Select(id => ChatKeys.Of(project, id)).ToList());

    public Task MessageReceivedAsync(string projectCode, IReadOnlyCollection<string> userIds, ChatMessageDto message, CancellationToken cancellationToken = default) =>
        To(projectCode, userIds).SendAsync("ReceiveMessage", message, cancellationToken);

    public Task MessageReadAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, string userId, DateTimeOffset readAt, CancellationToken cancellationToken = default) =>
        To(projectCode, userIds).SendAsync("MessageRead", conversationId, userId, readAt, cancellationToken);

    public Task TypingAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, string userId, bool isTyping, CancellationToken cancellationToken = default) =>
        To(projectCode, userIds).SendAsync("UserTyping", conversationId, userId, isTyping, cancellationToken);

    public Task PresenceChangedAsync(string projectCode, IReadOnlyCollection<string> userIds, string userId, bool isOnline, CancellationToken cancellationToken = default) =>
        To(projectCode, userIds).SendAsync("UserPresenceChanged", userId, isOnline, cancellationToken);

    public Task ConversationChangedAsync(string projectCode, IReadOnlyCollection<string> userIds, Guid conversationId, CancellationToken cancellationToken = default) =>
        To(projectCode, userIds).SendAsync("ConversationChanged", conversationId, cancellationToken);
}

/// <summary><c>Context.UserIdentifier</c> of a connection: "project:user", from the same resolver the REST endpoints use.</summary>
public sealed class ChatUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        var http = connection.GetHttpContext();
        return http is not null && ChatActorResolver.TryResolve(http, null, out var actor, out _) ? actor.Key : null;
    }
}

public static class HubAuthentication
{
    /// <summary>
    /// A browser cannot set headers on a WebSocket, so SignalR sends the token as <c>?access_token=</c>. For the chat hub only, and only when
    /// the call carries no header credential, it is moved into the Authorization header as a bearer token (an API key is never read from the
    /// address: a key is a server credential and addresses end up in logs). The token is then removed from the address.
    /// </summary>
    public static void MoveTokenFromQuery(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(ChatHub.Path)) return;
        if (context.Request.Headers.ContainsKey("Authorization") || context.Request.Headers.ContainsKey(ApiKeyDefaults.Header)) return;

        var token = context.Request.Query["access_token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(token)) context.Request.Headers.Authorization = $"Bearer {token}";

        // Whatever happens next in the pipeline (a log line, an exception page, a proxy header) never sees the token again: it leaves the address
        // as soon as it is in the header. The other parameters (the connection id, the negotiate version, the project) stay.
        var rest = context.Request.Query.Where(pair => !pair.Key.Equals("access_token", StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)));
        context.Request.QueryString = QueryString.Create(rest);
    }
}
