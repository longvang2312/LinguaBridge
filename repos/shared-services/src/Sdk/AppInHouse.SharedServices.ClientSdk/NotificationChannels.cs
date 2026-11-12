using System.Net.Http.Json;
using System.Text.Json;

namespace AppInHouse.SharedServices.ClientSdk;

/// <summary>
/// Save a notification channel. <see cref="ChannelType"/> left out means <c>Chat</c> (a bot: Telegram, Discord or Slack); <c>Email</c> is the
/// other notification channel. <see cref="Secret"/> (a bot token or a webhook address) is write-only: omit it, or send the mask back, to keep
/// the stored one.
/// </summary>
public sealed record SaveNotificationChannelRequest(
    string ProjectCode, string ProviderType, string Name, string? ChannelType = null, JsonElement? Settings = null, string? Secret = null);

public sealed record RollbackNotificationChannelRequest(string ProjectCode, string Name, string? ChannelType = null, int? TargetVersion = null);

/// <summary>
/// Send a message through the project's bot. <see cref="Recipient"/> is a chat id for Telegram, an optional channel for Slack, unused by
/// Discord; empty means the configuration's default. The project defaults to the project of the API key.
/// </summary>
public sealed record SendBotMessageRequest(
    string Text, string? Recipient = null, string? Format = null, string? ProjectCode = null, Dictionary<string, string>? Metadata = null);

public sealed record SendBotMessageResponse(
    bool Success, string Status, Guid? DeliveryLogId, int LatencyMs, string? ConfigurationProject, int? ConfigurationVersion);

/// <summary>Test a bot configuration: the saved version (<see cref="ConfigurationId"/>) or settings tried from memory (nothing is saved).</summary>
public sealed record TestBotChannelRequest(
    string? Recipient = null, string? Message = null, Guid? ConfigurationId = null,
    string? ProviderType = null, JsonElement? Settings = null, string? Secret = null);

/// <summary>A failed test is an answer (<see cref="Success"/> false with a short, safe <see cref="Message"/>), not an exception.</summary>
public sealed record TestBotChannelResponse(
    bool Success, string Message, int LatencyMs, string ProviderType, bool UsedSavedConfiguration, Guid? ConfigurationId);

// The notification channels of SharedServicesClient: the bots (Telegram, Discord, Slack) and email are channels of the notification service.
// The older chat address aliases (/chat/send, /chat/test, /telegram/test) are gone: the bots live only under /notifications/channels.
public sealed partial class SharedServicesClient
{
    private const string Channels = "api/v1/notifications/channels";

    public async Task<IReadOnlyList<ChannelConfigurationResponse>> GetNotificationChannelsAsync(
        string? projectCode = null, string kind = "bot", string? status = null, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"kind={Uri.EscapeDataString(kind)}" };
        if (!string.IsNullOrWhiteSpace(projectCode)) query.Add($"projectCode={Uri.EscapeDataString(projectCode)}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");

        using var response = await http.GetAsync($"{Channels}?{string.Join('&', query)}", cancellationToken);
        return await ReadAsync<List<ChannelConfigurationResponse>>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> GetNotificationChannelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Channels}/{id}", cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> SaveNotificationChannelAsync(SaveNotificationChannelRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(Channels, request, Json, cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> ActivateNotificationChannelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"{Channels}/{id}/activate", null, cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> RollbackNotificationChannelAsync(RollbackNotificationChannelRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"{Channels}/rollback", request, Json, cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<TestBotChannelResponse> TestNotificationChannelAsync(TestBotChannelRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"{Channels}/test", request, Json, cancellationToken);
        return await ReadAsync<TestBotChannelResponse>(response, cancellationToken);
    }

    public async Task<SendBotMessageResponse> SendNotificationChannelMessageAsync(SendBotMessageRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"{Channels}/send", request, Json, cancellationToken);
        return await ReadAsync<SendBotMessageResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryLogDto>> GetBotDeliveryLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        var query = $"limit={limit}" + (string.IsNullOrWhiteSpace(projectCode) ? string.Empty : $"&projectCode={Uri.EscapeDataString(projectCode)}");
        using var response = await http.GetAsync($"{Channels}/delivery-logs?{query}", cancellationToken);
        return await ReadAsync<List<DeliveryLogDto>>(response, cancellationToken);
    }
}
