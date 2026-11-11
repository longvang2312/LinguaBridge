using System.Text.Json;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppInHouse.SharedServices.IntegrationTests.Support;

/// <summary>A stand-in for the API behind the portal: canned answers, every call recorded, any call can be made to fail.</summary>
public sealed class FakeSharedServicesClient : ISharedServicesClient
{
    public List<ChannelConfigurationResponse> Items { get; set; } = [];
    public Exception? Failure { get; set; }
    public List<string> Calls { get; } = [];
    public List<DeliveryLogDto> Logs { get; set; } = [];
    public SendTestEmailResponse? TestResponse { get; set; }
    public SendTestEmailRequest? LastTestRequest { get; private set; }
    public SaveChannelConfigurationRequest? LastSave { get; private set; }
    public List<DeliveryLogDto> ChatLogs { get; set; } = [];
    public SendTestChatMessageResponse? ChatTestResponse { get; set; }
    public SendTestChatMessageRequest? LastChatTestRequest { get; private set; }

    private void Enter(string call)
    {
        Calls.Add(call);
        if (Failure is not null) throw Failure;
    }

    public Task<IReadOnlyList<ChannelConfigurationResponse>> ListConfigurationsAsync(string? projectCode = null, string? channelType = null, string? status = null, CancellationToken cancellationToken = default)
    {
        Enter($"list:{projectCode}:{channelType}:{status}");
        return Task.FromResult<IReadOnlyList<ChannelConfigurationResponse>>([.. Items.Where(c =>
            (string.IsNullOrWhiteSpace(projectCode) || c.ProjectCode == projectCode)
            && (string.IsNullOrWhiteSpace(channelType) || c.ChannelType.Equals(channelType, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(status) || c.Status.Equals(status, StringComparison.OrdinalIgnoreCase)))]);
    }

    public Task<ChannelConfigurationResponse> GetConfigurationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Enter($"get:{id}");
        return Task.FromResult(Items.Single(i => i.Id == id));
    }

    public Task<ChannelConfigurationResponse> SaveConfigurationAsync(SaveChannelConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        Enter($"save:{request.Name}");
        LastSave = request;
        var saved = Items.FirstOrDefault() ?? WebFactory.Config(request.Name, 1, "Draft");
        return Task.FromResult(saved with { Name = request.Name, ProviderType = request.ProviderType });
    }

    public Task<ChannelConfigurationResponse> ActivateConfigurationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Enter($"activate:{id}");
        return Task.FromResult(Items.Single(i => i.Id == id) with { Status = "Active" });
    }

    public Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
    {
        Enter($"send-email:{request.To}");
        return Task.FromResult(new SendEmailResponse(true, "Sent", Guid.NewGuid(), 1, "default", 1));
    }

    public Task<SendTestEmailResponse> SendTestEmailAsync(SendTestEmailRequest request, CancellationToken cancellationToken = default)
    {
        Enter($"test-email:{request.ConfigurationId?.ToString() ?? "unsaved"}:{request.ProviderType}");
        LastTestRequest = request;
        return Task.FromResult(TestResponse ?? new SendTestEmailResponse(true, "Test email accepted.", 12, request.ProviderType ?? "Smtp", request.ConfigurationId is not null, request.ConfigurationId));
    }

    public Task<IReadOnlyList<DeliveryLogDto>> GetEmailDeliveryLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        Enter($"logs:{projectCode}:{limit}");
        return Task.FromResult<IReadOnlyList<DeliveryLogDto>>(Logs);
    }

    public Task<SendChatMessageResponse> SendChatMessageAsync(SendChatMessageRequest request, CancellationToken cancellationToken = default)
    {
        Enter($"send-chat:{request.Text}");
        return Task.FromResult(new SendChatMessageResponse(true, "Sent", Guid.NewGuid(), 1, "default", 1));
    }

    public Task<SendTestChatMessageResponse> SendTestChatMessageAsync(SendTestChatMessageRequest request, CancellationToken cancellationToken = default)
    {
        Enter($"test-chat:{request.ConfigurationId?.ToString() ?? "unsaved"}:{request.ProviderType}");
        LastChatTestRequest = request;
        return Task.FromResult(ChatTestResponse ?? new SendTestChatMessageResponse(true, "Test message accepted.", 9, request.ProviderType ?? "TelegramBot", request.ConfigurationId is not null, request.ConfigurationId));
    }

    public Task<IReadOnlyList<DeliveryLogDto>> GetChatDeliveryLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        Enter($"chat-logs:{projectCode}:{limit}");
        return Task.FromResult<IReadOnlyList<DeliveryLogDto>>(ChatLogs);
    }

    // ---- API keys and audit

    public List<ApiKeyDto> ApiKeys { get; set; } = [];
    public List<AuditLogDto> AuditLogs { get; set; } = [];
    public List<string> AuditActions { get; set; } = [];
    public CreateApiKeyRequest? LastCreateKey { get; private set; }
    public (string? Actor, string? Action, DateTimeOffset? From, DateTimeOffset? To, int Limit)? LastAuditQuery { get; private set; }

    /// <summary>The full key the fake hands out when a key is created or rotated.</summary>
    public const string IssuedKey = "aih_live_abc123Fakeissuedkeyvalue0123456789ABCDEFGH";

    public Task<CreateApiKeyResponse> CreateApiKeyAsync(CreateApiKeyRequest request, CancellationToken cancellationToken = default)
    {
        Enter($"create-key:{request.Name}");
        LastCreateKey = request;
        var key = new ApiKeyDto(Guid.NewGuid(), request.ProjectCode, request.Name, "aih_live_abc123", request.Scopes, "Active", DateTimeOffset.UtcNow,
            request.ExpiresInDays is { } days ? DateTimeOffset.UtcNow.AddDays(days) : null, null);
        ApiKeys.Add(key);
        return Task.FromResult(new CreateApiKeyResponse(key, IssuedKey));
    }

    public Task<IReadOnlyList<ApiKeyDto>> ListApiKeysAsync(string? projectCode = null, CancellationToken cancellationToken = default)
    {
        Enter($"list-keys:{projectCode}");
        return Task.FromResult<IReadOnlyList<ApiKeyDto>>([.. ApiKeys.Where(k => string.IsNullOrEmpty(projectCode) || k.ProjectCode == projectCode)]);
    }

    public Task<ApiKeyDto> RevokeApiKeyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Enter($"revoke-key:{id}");
        var index = ApiKeys.FindIndex(k => k.Id == id);
        if (index < 0) throw new SharedServicesApiException(404, "API key does not exist.");
        ApiKeys[index] = ApiKeys[index] with { Status = "Revoked" };
        return Task.FromResult(ApiKeys[index]);
    }

    public Task<RotateApiKeyResponse> RotateApiKeyAsync(Guid id, int graceDays = 7, CancellationToken cancellationToken = default)
    {
        Enter($"rotate-key:{id}:{graceDays}");
        var old = ApiKeys.First(k => k.Id == id) with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(graceDays) };
        var fresh = old with { Id = Guid.NewGuid(), KeyPrefix = "aih_live_new999", ExpiresAt = null };
        ApiKeys.Add(fresh);
        return Task.FromResult(new RotateApiKeyResponse(fresh, IssuedKey, old));
    }

    public Task<IReadOnlyList<AuditLogDto>> ListAuditLogsAsync(string? actor = null, string? action = null, DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        Enter($"audit:{actor}:{action}");
        LastAuditQuery = (actor, action, from, to, limit);
        return Task.FromResult<IReadOnlyList<AuditLogDto>>([.. AuditLogs.Where(l =>
            (actor is null || l.Actor.Contains(actor)) && (action is null || l.Action == action) && (from is null || l.CreatedAt >= from) && (to is null || l.CreatedAt < to))]);
    }

    public Task<IReadOnlyList<string>> GetAuditActionsAsync(CancellationToken cancellationToken = default)
    {
        Enter("audit-actions");
        return Task.FromResult<IReadOnlyList<string>>(AuditActions);
    }

    // ---- storage

    public List<StoredFileDto> StoredFiles { get; set; } = [];
    public List<MediaLogDto> MediaLogs { get; set; } = [];
    public StorageTestResponse? StorageTestResult { get; set; }

    /// <summary>The bytes the fake serves for any file; null means the file is missing from the storage.</summary>
    public byte[]? FileBytes { get; set; } = [0x89, 0x50, 0x4E, 0x47];

    public (UploadFileRequest Request, byte[] Content)? LastUpload { get; private set; }
    public StorageTestRequest? LastStorageTestRequest { get; private set; }

    public async Task<UploadFileResponse> UploadFileAsync(UploadFileRequest request, Stream fileStream, CancellationToken cancellationToken = default)
    {
        Enter($"upload:{request.FileName}:{request.ProjectCode}:{request.IsPublic}");
        using var copy = new MemoryStream();
        await fileStream.CopyToAsync(copy, cancellationToken);
        LastUpload = (request, copy.ToArray());
        var file = new StoredFileDto(Guid.NewGuid(), request.ProjectCode ?? "default", "LocalDisk", request.FileName, request.ContentType ?? "image/png", copy.Length, new string('a', 64), request.IsPublic, true, DateTimeOffset.UtcNow, "/api/v1/storage/files/x", "/api/v1/storage/files/x/thumbnail");
        return new UploadFileResponse(file, file.Url, file.ThumbnailUrl, null);
    }

    public Task<FileStreamResponse> DownloadFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        Enter($"download:{fileId}");
        var file = StoredFiles.FirstOrDefault(f => f.Id == fileId) ?? throw new SharedServicesApiException(404, "The file does not exist.");
        return Task.FromResult(new FileStreamResponse(new MemoryStream(FileBytes ?? []), file.MimeType, file.OriginalFileName, FileBytes?.Length, "\"" + file.FileHashSha256 + "\""));
    }

    public Task<FileStreamResponse?> GetThumbnailAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        Enter($"thumb:{fileId}");
        var file = StoredFiles.FirstOrDefault(f => f.Id == fileId);
        return Task.FromResult(file is { HasThumbnail: true }
            ? new FileStreamResponse(new MemoryStream([0x52, 0x49, 0x46, 0x46]), "image/webp", "thumb.webp", 4, null)
            : null);
    }

    public Task<StoredFileDto?> GetFileMetadataAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        Enter($"metadata:{fileId}");
        return Task.FromResult(StoredFiles.FirstOrDefault(f => f.Id == fileId));
    }

    public Task<IReadOnlyList<StoredFileDto>> ListFilesAsync(string? projectCode = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        Enter($"files:{projectCode}:{limit}");
        return Task.FromResult<IReadOnlyList<StoredFileDto>>([.. StoredFiles.Where(f => projectCode is null || f.ProjectCode == projectCode).Take(limit)]);
    }

    public Task<bool> DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        Enter($"delete-file:{fileId}");
        return Task.FromResult(StoredFiles.RemoveAll(f => f.Id == fileId) > 0);
    }

    public Task<StorageTestResponse> TestStorageAsync(StorageTestRequest request, CancellationToken cancellationToken = default)
    {
        Enter($"test-storage:{request.ConfigurationId?.ToString() ?? "unsaved"}:{request.ProviderType}");
        LastStorageTestRequest = request;
        return Task.FromResult(StorageTestResult ?? new StorageTestResponse(true, "The folder can be written and read.", 7, request.ProviderType ?? "LocalDisk", request.ConfigurationId is not null, request.ConfigurationId));
    }

    public Task<IReadOnlyList<MediaLogDto>> GetMediaLogsAsync(string? projectCode = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        Enter($"media-logs:{projectCode}:{limit}");
        return Task.FromResult<IReadOnlyList<MediaLogDto>>(MediaLogs);
    }

    public Task<ChannelConfigurationResponse> RollbackConfigurationAsync(RollbackConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        Enter($"rollback:{request.ProjectCode}:{request.ChannelType}:{request.Name}:{request.TargetVersion}");
        return Task.FromResult(Items.First(i => i.Name == request.Name && i.Version == request.TargetVersion) with { Status = "Active" });
    }
}

public sealed class WebFactory : WebApplicationFactory<WebEntryPoint>
{
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "ss-web-" + Guid.NewGuid().ToString("N"));

    public FakeSharedServicesClient Api { get; } = new();

    public static ChannelConfigurationResponse Config(
        string name, int version, string status, string project = "linguabridge", string channel = "Email", string provider = "Smtp", bool hasSecret = true) =>
        new(Guid.NewGuid(), project, channel, provider, name, version, status,
            JsonDocument.Parse("""{"host":"smtp.example.com","port":587}""").RootElement.Clone(),
            hasSecret, hasSecret ? "••••••••" : string.Empty, "Untested", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DataProtection:KeysPath", _keys);
        builder.UseSetting("Authentication:Enabled", "false"); // these tests are about the pages; WebAuthenticationTests covers sign-in
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISharedServicesClient>();
            services.AddSingleton<ISharedServicesClient>(Api);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { if (Directory.Exists(_keys)) Directory.Delete(_keys, recursive: true); } catch (IOException) { }
    }
}
