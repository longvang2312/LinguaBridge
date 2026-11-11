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
