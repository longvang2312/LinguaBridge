using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.UnitTests.Support;

/// <summary>A reversible stand-in whose output is obviously not the input and never contains it.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public const string Prefix = "protected::";

    public string Protect(string plainText) => Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string(plainText.Reverse().ToArray())));

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal)) throw new SecretProtectionException("not protected");
        return new string(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[Prefix.Length..])).Reverse().ToArray());
    }
}

internal sealed class FakeChannelConfigRepository : IChannelConfigRepository
{
    public List<ChannelConfiguration> Rows { get; } = [];

    public Task<IReadOnlyList<ChannelConfiguration>> GetSeriesAsync(string projectCode, ChannelType channelType, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChannelConfiguration>>(
            [.. Rows.Where(c => c.ProjectCode == projectCode && c.ChannelType == channelType && c.Name == name)]);

    public Task<ChannelConfiguration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Rows.FirstOrDefault(c => c.Id == id));

    public Task<IReadOnlyList<ChannelConfiguration>> ListAsync(string? projectCode, ChannelType? channelType, ConfigurationStatus? status, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChannelConfiguration>>([.. Rows.Where(c =>
            (projectCode is null || c.ProjectCode == projectCode)
            && (channelType is null || c.ChannelType == channelType)
            && (status is null || c.Status == status))]);

    public Task AddAsync(ChannelConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Rows.Add(configuration);
        return Task.CompletedTask;
    }
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        return Task.FromResult(1);
    }
}

internal sealed class FakeAuditLogWriter : IAuditLogWriter
{
    public sealed record Entry(string Action, string ResourceType, string ResourceId, string DetailsJson);

    public List<Entry> Entries { get; } = [];

    public void Record(string action, string resourceType, string resourceId, object? details = null) =>
        Entries.Add(new Entry(action, resourceType, resourceId, details is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(details)));
}

internal sealed class FixedClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A manager wired to in-memory fakes, with the fakes exposed so a test can look at what happened.</summary>
internal sealed class ManagerFixture
{
    public ManagerFixture()
    {
        Clock = new FixedClock(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero));
        Manager = new ChannelConfigManager(Repository, Protector, Audit, UnitOfWork, Clock);
    }

    public FakeChannelConfigRepository Repository { get; } = new();
    public FakeSecretProtector Protector { get; } = new();
    public FakeAuditLogWriter Audit { get; } = new();
    public FakeUnitOfWork UnitOfWork { get; } = new();
    public FixedClock Clock { get; }
    public ChannelConfigManager Manager { get; }

    public static SaveChannelConfigurationCommand SmtpCommand(string settings = """{"host":"smtp.example.com","port":587}""", string? secret = "S3cr3t-P@ssw0rd") =>
        new("default", ChannelType.Email, ProviderType.Smtp, "Main SMTP", settings, secret);
}

internal sealed class FakeDeliveryLogRepository : AppInHouse.SharedServices.Application.Email.IDeliveryLogRepository
{
    public List<DeliveryLog> Rows { get; } = [];

    public void Add(DeliveryLog log) => Rows.Add(log);

    public Task<IReadOnlyList<DeliveryLog>> ListRecentAsync(string? projectCode, ChannelType channelType, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeliveryLog>>([.. Rows
            .Where(l => l.ChannelType == channelType && (projectCode is null || l.ProjectCode == projectCode))
            .OrderByDescending(l => l.CreatedAt).Take(limit)]);
}

/// <summary>An email provider that records what it was given and answers as told.</summary>
internal sealed class FakeEmailProvider(ProviderType type) : AppInHouse.SharedServices.Application.Email.IEmailProvider
{
    public ProviderType Type { get; } = type;
    public List<(AppInHouse.SharedServices.Application.Email.EmailMessage Message, ChannelRuntimeConfiguration Config)> Sent { get; } = [];
    public List<(ChannelRuntimeConfiguration Config, string Recipient)> Tested { get; } = [];
    public AppInHouse.SharedServices.Application.Email.EmailSendResult SendResult { get; set; } = new(true, "msg-1", ProviderResponse: "250 queued");
    public AppInHouse.SharedServices.Application.Email.EmailTestResult TestResult { get; set; } = new(true, "Test email accepted.", 5);
    public Exception? Throw { get; set; }

    public Task<AppInHouse.SharedServices.Application.Email.EmailSendResult> SendAsync(
        AppInHouse.SharedServices.Application.Email.EmailMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default)
    {
        Sent.Add((message, config));
        if (Throw is not null) throw Throw;
        return Task.FromResult(SendResult);
    }

    public Task<AppInHouse.SharedServices.Application.Email.EmailTestResult> TestConnectionAsync(ChannelRuntimeConfiguration config, string testRecipient, CancellationToken ct = default)
    {
        Tested.Add((config, testRecipient));
        if (Throw is not null) throw Throw;
        return Task.FromResult(TestResult);
    }
}

internal static class RuntimeConfig
{
    public static ChannelRuntimeConfiguration Make(ProviderType provider, string settingsJson, string? secret, string project = "default") => new()
    {
        Id = Guid.NewGuid(),
        ProjectCode = project,
        ChannelType = ChannelType.Email,
        ProviderType = provider,
        Name = "Test",
        Version = 1,
        Settings = System.Text.Json.JsonDocument.Parse(settingsJson).RootElement.Clone(),
        Secret = secret,
    };

    public const string Smtp = """{"host":"203.0.113.10","port":2525,"useSsl":"None","username":"mailer","senderEmail":"noreply@example.com","senderName":"AppInHouse"}""";
    public const string SendGrid = """{"senderEmail":"noreply@example.com","senderName":"AppInHouse"}""";
}
