using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppInHouse.SharedServices.ClientSdk;

public interface ISharedServicesClient
{
    Task<IReadOnlyList<ChannelConfigurationResponse>> ListConfigurationsAsync(
        string? projectCode = null, string? channelType = null, string? status = null, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationResponse> GetConfigurationAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationResponse> SaveConfigurationAsync(SaveChannelConfigurationRequest request, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationResponse> ActivateConfigurationAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ChannelConfigurationResponse> RollbackConfigurationAsync(RollbackConfigurationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends an email with the project's active email configuration. A refusal or failure throws <see cref="SharedServicesApiException"/> (409 no configuration, 502 not delivered).</summary>
    Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default);

    Task<SendTestEmailResponse> SendTestEmailAsync(SendTestEmailRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeliveryLogDto>> GetEmailDeliveryLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>Sends a chat message with the project's active chat configuration. A refusal or failure throws <see cref="SharedServicesApiException"/> (409 no configuration, 502 not delivered).</summary>
    Task<SendChatMessageResponse> SendChatMessageAsync(SendChatMessageRequest request, CancellationToken cancellationToken = default);

    Task<SendTestChatMessageResponse> SendTestChatMessageAsync(SendTestChatMessageRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeliveryLogDto>> GetChatDeliveryLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>Stores a file. A refusal throws <see cref="SharedServicesApiException"/> (400 not an allowed type or not what it claims to be, 409 no storage configuration, 413 too large, 502 not stored).</summary>
    Task<UploadFileResponse> UploadFileAsync(UploadFileRequest request, Stream fileStream, CancellationToken cancellationToken = default);

    /// <summary>Streams a file. Throws <see cref="SharedServicesApiException"/> when it does not exist (404) or the key may not read it (403).</summary>
    Task<FileStreamResponse> DownloadFileAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>Streams the thumbnail of an image; null when the file has none (or does not exist).</summary>
    Task<FileStreamResponse?> GetThumbnailAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>What is known about a file; null when it does not exist.</summary>
    Task<StoredFileDto?> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredFileDto>> ListFilesAsync(string? projectCode = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file; false when it did not exist.</summary>
    Task<bool> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default);

    Task<StorageTestResponse> TestStorageAsync(StorageTestRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaLogDto>> GetMediaLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>Creates a key (only a platform key may). The full key is in the answer and is never available again.</summary>
    Task<CreateApiKeyResponse> CreateApiKeyAsync(CreateApiKeyRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiKeyDto>> ListApiKeysAsync(string? projectCode = null, CancellationToken cancellationToken = default);

    /// <summary>Revokes a key at once (only a platform key may).</summary>
    Task<ApiKeyDto> RevokeApiKeyAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Makes a new key like the old one; the old one works for <paramref name="graceDays"/> more days, then stops.</summary>
    Task<RotateApiKeyResponse> RotateApiKeyAsync(Guid id, int graceDays = 7, CancellationToken cancellationToken = default);

    /// <summary>The audit log, newest first (the platform's keys only). <paramref name="to"/> is exclusive.</summary>
    Task<IReadOnlyList<AuditLogDto>> ListAuditLogsAsync(
        string? actor = null, string? action = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 50, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetAuditActionsAsync(CancellationToken cancellationToken = default);
}

public sealed class SharedServicesClient(HttpClient http) : ISharedServicesClient
{
    private const string Root = "api/v1/channel-configurations";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<ChannelConfigurationResponse>> ListConfigurationsAsync(
        string? projectCode = null, string? channelType = null, string? status = null, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectCode)) query.Add($"projectCode={Uri.EscapeDataString(projectCode)}");
        if (!string.IsNullOrWhiteSpace(channelType)) query.Add($"channelType={Uri.EscapeDataString(channelType)}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");

        using var response = await http.GetAsync(query.Count == 0 ? Root : $"{Root}?{string.Join('&', query)}", cancellationToken);
        return await ReadAsync<List<ChannelConfigurationResponse>>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> GetConfigurationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Root}/{id}", cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> SaveConfigurationAsync(SaveChannelConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(Root, request, Json, cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> ActivateConfigurationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"{Root}/{id}/activate", content: null, cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<ChannelConfigurationResponse> RollbackConfigurationAsync(RollbackConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"{Root}/rollback", request, Json, cancellationToken);
        return await ReadAsync<ChannelConfigurationResponse>(response, cancellationToken);
    }

    public async Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/emails/send", request, Json, cancellationToken);
        return await ReadAsync<SendEmailResponse>(response, cancellationToken);
    }

    public async Task<SendTestEmailResponse> SendTestEmailAsync(SendTestEmailRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/emails/test", request, Json, cancellationToken);
        return await ReadAsync<SendTestEmailResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryLogDto>> GetEmailDeliveryLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        var query = $"limit={limit}" + (string.IsNullOrWhiteSpace(projectCode) ? string.Empty : $"&projectCode={Uri.EscapeDataString(projectCode)}");
        using var response = await http.GetAsync($"api/v1/emails/delivery-logs?{query}", cancellationToken);
        return await ReadAsync<List<DeliveryLogDto>>(response, cancellationToken);
    }

    public async Task<SendChatMessageResponse> SendChatMessageAsync(SendChatMessageRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/chat/send", request, Json, cancellationToken);
        return await ReadAsync<SendChatMessageResponse>(response, cancellationToken);
    }

    public async Task<SendTestChatMessageResponse> SendTestChatMessageAsync(SendTestChatMessageRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/chat/test", request, Json, cancellationToken);
        return await ReadAsync<SendTestChatMessageResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryLogDto>> GetChatDeliveryLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        var query = $"limit={limit}" + (string.IsNullOrWhiteSpace(projectCode) ? string.Empty : $"&projectCode={Uri.EscapeDataString(projectCode)}");
        using var response = await http.GetAsync($"api/v1/chat/delivery-logs?{query}", cancellationToken);
        return await ReadAsync<List<DeliveryLogDto>>(response, cancellationToken);
    }

    public async Task<UploadFileResponse> UploadFileAsync(UploadFileRequest request, Stream fileStream, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(fileStream);
        if (!string.IsNullOrWhiteSpace(request.ContentType)) file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(request.FileName) ? "file" : request.FileName);
        form.Add(new StringContent(request.IsPublic ? "true" : "false"), "isPublic");
        if (!string.IsNullOrWhiteSpace(request.ProjectCode)) form.Add(new StringContent(request.ProjectCode), "projectCode");

        using var response = await http.PostAsync("api/v1/storage/upload", form, cancellationToken);
        return await ReadAsync<UploadFileResponse>(response, cancellationToken);
    }

    public async Task<FileStreamResponse> DownloadFileAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        await StreamAsync($"api/v1/storage/files/{fileId}", cancellationToken)
        ?? throw new SharedServicesApiException(404, "The file does not exist.");

    public Task<FileStreamResponse?> GetThumbnailAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        StreamAsync($"api/v1/storage/files/{fileId}/thumbnail", cancellationToken);

    public async Task<StoredFileDto?> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/storage/files/{fileId}/metadata", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<StoredFileDto>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredFileDto>> ListFilesAsync(string? projectCode = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var query = $"limit={limit}" + (string.IsNullOrWhiteSpace(projectCode) ? string.Empty : $"&projectCode={Uri.EscapeDataString(projectCode)}");
        using var response = await http.GetAsync($"api/v1/storage/files?{query}", cancellationToken);
        return await ReadAsync<List<StoredFileDto>>(response, cancellationToken);
    }

    public async Task<bool> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/storage/files/{fileId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, cancellationToken);
        return true;
    }

    public async Task<StorageTestResponse> TestStorageAsync(StorageTestRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/storage/test", request, Json, cancellationToken);
        return await ReadAsync<StorageTestResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<MediaLogDto>> GetMediaLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        var query = $"limit={limit}" + (string.IsNullOrWhiteSpace(projectCode) ? string.Empty : $"&projectCode={Uri.EscapeDataString(projectCode)}");
        using var response = await http.GetAsync($"api/v1/storage/media-logs?{query}", cancellationToken);
        return await ReadAsync<List<MediaLogDto>>(response, cancellationToken);
    }

    public async Task<CreateApiKeyResponse> CreateApiKeyAsync(CreateApiKeyRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/api-keys", request, Json, cancellationToken);
        return await ReadAsync<CreateApiKeyResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ApiKeyDto>> ListApiKeysAsync(string? projectCode = null, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(projectCode) ? "api/v1/api-keys" : $"api/v1/api-keys?projectCode={Uri.EscapeDataString(projectCode)}";
        using var response = await http.GetAsync(url, cancellationToken);
        return await ReadAsync<List<ApiKeyDto>>(response, cancellationToken);
    }

    public async Task<ApiKeyDto> RevokeApiKeyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/api-keys/{id}/revoke", null, cancellationToken);
        return await ReadAsync<ApiKeyDto>(response, cancellationToken);
    }

    public async Task<RotateApiKeyResponse> RotateApiKeyAsync(Guid id, int graceDays = 7, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/api-keys/{id}/rotate?graceDays={graceDays}", null, cancellationToken);
        return await ReadAsync<RotateApiKeyResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogDto>> ListAuditLogsAsync(
        string? actor = null, string? action = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrWhiteSpace(actor)) query.Add($"actor={Uri.EscapeDataString(actor)}");
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action)}");
        if (from is { } start) query.Add($"from={Uri.EscapeDataString(start.ToString("O"))}");
        if (to is { } end) query.Add($"to={Uri.EscapeDataString(end.ToString("O"))}");
        using var response = await http.GetAsync($"api/v1/audit-logs?{string.Join('&', query)}", cancellationToken);
        return await ReadAsync<List<AuditLogDto>>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetAuditActionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/audit-logs/actions", cancellationToken);
        return await ReadAsync<List<string>>(response, cancellationToken);
    }

    /// <summary>A body to stream: the response stays open until the caller disposes the result. A 404 is null.</summary>
    private async Task<FileStreamResponse?> StreamAsync(string url, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return null;
            }

            if (!response.IsSuccessStatusCode) throw await ToExceptionAsync(response, cancellationToken);

            var disposition = response.Content.Headers.ContentDisposition;
            var name = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"') ?? "file";
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return new FileStreamResponse(stream, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", name,
                response.Content.Headers.ContentLength, response.Headers.ETag?.Tag, owner: response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
                   ?? throw new SharedServicesApiException((int)response.StatusCode, "The service returned an empty answer.");

        throw await ToExceptionAsync(response, cancellationToken);
    }

    // The API answers with RFC 7807 problem details; turn them into one exception that carries the status and the field errors.
    private static async Task<SharedServicesApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? title = null, detail = null;
        Dictionary<string, string[]>? errors = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                    errors = e.Deserialize<Dictionary<string, string[]>>(Json);
            }
        }
        catch (JsonException)
        {
            // Not problem details (a proxy's error page, for example): fall back to the status line.
        }

        var message = detail ?? title ?? response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The service did not accept the API key.",
            HttpStatusCode.Forbidden => "The API key is not allowed to do this.",
            _ => $"The service answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
        return new SharedServicesApiException((int)response.StatusCode, message, errors);
    }
}
