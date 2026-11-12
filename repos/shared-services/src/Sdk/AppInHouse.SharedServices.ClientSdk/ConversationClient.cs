using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AppInHouse.SharedServices.ClientSdk;

// The conversation calls of SharedServicesClient. A call is made for a user (the chat user headers); see ChatUser.
public sealed partial class SharedServicesClient
{
    private const string Conversations = "api/v1/chat/conversations";

    public async Task<CreateConversationResponse> CreateConversationAsync(ChatUser user, CreateConversationRequest request, CancellationToken cancellationToken = default)
    {
        var body = new { request.Type, request.Title, request.Members, request.Self, user.ProjectCode };
        using var response = await SendAsUserAsync(HttpMethod.Post, Conversations, user, body, cancellationToken);
        var conversation = await ReadAsync<ConversationDto>(response, cancellationToken);
        return new CreateConversationResponse(conversation, response.StatusCode == HttpStatusCode.Created);
    }

    public async Task<IReadOnlyList<ConversationSummaryDto>> GetConversationsAsync(ChatUser user, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsUserAsync(HttpMethod.Get, WithProject(Conversations, user), user, null, cancellationToken);
        return await ReadAsync<List<ConversationSummaryDto>>(response, cancellationToken);
    }

    public async Task<MessagePageDto> GetConversationMessagesAsync(
        ChatUser user, Guid conversationId, DateTimeOffset? before = null, Guid? beforeId = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (before is { } b) query.Add($"before={Uri.EscapeDataString(b.ToString("O", CultureInfo.InvariantCulture))}");
        if (beforeId is { } id) query.Add($"beforeId={id}");
        if (limit is { } l) query.Add($"limit={l.ToString(CultureInfo.InvariantCulture)}");
        using var response = await SendAsUserAsync(HttpMethod.Get, WithProject($"{Conversations}/{conversationId}/messages", user, query), user, null, cancellationToken);
        return await ReadAsync<MessagePageDto>(response, cancellationToken);
    }

    public async Task<ChatMessageDto> SendConversationMessageAsync(ChatUser user, Guid conversationId, string content, Guid? attachmentFileId = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsUserAsync(HttpMethod.Post, WithProject($"{Conversations}/{conversationId}/messages", user), user, new { content, attachmentFileId }, cancellationToken);
        return await ReadAsync<ChatMessageDto>(response, cancellationToken);
    }

    public async Task<DateTimeOffset> MarkConversationReadAsync(ChatUser user, Guid conversationId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsUserAsync(HttpMethod.Post, WithProject($"{Conversations}/{conversationId}/read", user), user, null, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("readAt").GetDateTimeOffset();
    }

    public async Task<ConversationDto> RenameConversationAsync(ChatUser user, Guid conversationId, string title, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsUserAsync(HttpMethod.Put, WithProject($"{Conversations}/{conversationId}", user), user, new { title }, cancellationToken);
        return await ReadAsync<ConversationDto>(response, cancellationToken);
    }

    public async Task<ConversationDto> AddConversationMembersAsync(ChatUser user, Guid conversationId, IReadOnlyList<ChatMemberRequest> members, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsUserAsync(HttpMethod.Post, WithProject($"{Conversations}/{conversationId}/members", user), user, new { members }, cancellationToken);
        return await ReadAsync<ConversationDto>(response, cancellationToken);
    }

    public async Task RemoveConversationMemberAsync(ChatUser user, Guid conversationId, string userId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsUserAsync(HttpMethod.Delete, WithProject($"{Conversations}/{conversationId}/members/{Uri.EscapeDataString(userId)}", user), user, null, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, cancellationToken);
    }

    public async Task<ChatMonitorDto> GetChatMonitorAsync(string? projectCode = null, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(projectCode) ? "api/v1/chat/monitor" : $"api/v1/chat/monitor?projectCode={Uri.EscapeDataString(projectCode)}";
        using var response = await http.GetAsync(url, cancellationToken);
        return await ReadAsync<ChatMonitorDto>(response, cancellationToken);
    }

    public async Task<RealtimeCheckResult> CheckRealtimeAsync(CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        int Elapsed() => (int)Math.Min(started.ElapsedMilliseconds, int.MaxValue);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "hubs/chat/negotiate?negotiateVersion=1");
            request.Headers.Add("X-Chat-User", "portal-probe");
            using var response = await http.SendAsync(request, cancellationToken);
            var status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new RealtimeCheckResult(false, "The API key of this portal may not use the chat (it needs the chat:converse scope).", [], Elapsed(), status);
            if (!response.IsSuccessStatusCode)
                return new RealtimeCheckResult(false, $"The hub answered {status}.", [], Elapsed(), status);

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var transports = document.RootElement.TryGetProperty("availableTransports", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(t => t.TryGetProperty("transport", out var n) ? n.GetString() ?? string.Empty : string.Empty).Where(n => n.Length > 0).ToList()
                : [];
            var webSockets = transports.Contains("WebSockets", StringComparer.OrdinalIgnoreCase);
            return new RealtimeCheckResult(true, webSockets ? "The hub is reachable and offers WebSockets." : "The hub is reachable, but WebSockets are not offered.", transports, Elapsed(), status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new RealtimeCheckResult(false, "The hub could not be reached.", [], Elapsed(), null);
        }
    }

    private static string WithProject(string url, ChatUser user, List<string>? query = null)
    {
        query ??= [];
        if (!string.IsNullOrWhiteSpace(user.ProjectCode)) query.Add($"projectCode={Uri.EscapeDataString(user.ProjectCode)}");
        return query.Count == 0 ? url : $"{url}?{string.Join('&', query)}";
    }

    private Task<HttpResponseMessage> SendAsUserAsync(HttpMethod method, string url, ChatUser user, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Chat-User", user.UserId);
        if (!string.IsNullOrWhiteSpace(user.DisplayName)) request.Headers.Add("X-Chat-User-Name", SafeHeader(user.DisplayName));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return http.SendAsync(request, cancellationToken);
    }

    // A header value cannot carry line breaks or accented letters as they are, so the name is sent percent-encoded (UTF-8); the API decodes it.
    private static string SafeHeader(string value) => Uri.EscapeDataString(value);
}
