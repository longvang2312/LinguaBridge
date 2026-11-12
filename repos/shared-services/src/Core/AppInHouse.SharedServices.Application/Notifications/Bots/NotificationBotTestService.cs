using System.Diagnostics;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Security;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Notifications.Bots;

/// <summary>
/// "Send Test" for chat: posts a short message with a configuration to see that it works.
/// <list type="bullet">
/// <item>Without an id the provider, settings and secret in the request are used straight from memory. Nothing is stored and nothing is
/// activated: no configuration is created or changed (only an audit entry says who pressed the button).</item>
/// <item>With an id the saved version is tested, and its last test time and result are updated.</item>
/// </list>
/// The two are never mixed: a stored token or webhook address is not used with settings from the request.
/// </summary>
public sealed class NotificationBotTestService(
    IChannelConfigManager configurations,
    IEnumerable<IBotProvider> providers,
    IAuditLogWriter audit,
    IUnitOfWork unitOfWork,
    ILogger<NotificationBotTestService> logger) : INotificationBotTestService
{
    public const string DefaultTestMessage = "Tin nhắn thử nghiệm từ AppInHouse Shared Services. Nếu bạn đọc được dòng này, cấu hình chat hoạt động.";
    public const int MaxTestMessageLength = 1000;

    public async Task<TestBotResult> TestAsync(TestBotCommand command, CancellationToken cancellationToken = default)
    {
        var recipient = string.IsNullOrWhiteSpace(command.TestRecipient) ? string.Empty : command.TestRecipient.Trim();
        var text = string.IsNullOrWhiteSpace(command.TestMessage) ? DefaultTestMessage : command.TestMessage;

        var errors = new Dictionary<string, string[]>();
        if (recipient.Length > 100 || recipient.Any(char.IsControl)) errors["TestRecipient"] = ["The recipient must be at most 100 characters, on one line."];
        if (text.Length > MaxTestMessageLength) errors["TestMessage"] = [$"The test message is longer than {MaxTestMessageLength} characters."];
        if (errors.Count > 0) throw new ValidationException(errors);

        return command.ConfigurationId is { } id
            ? await TestSavedAsync(id, recipient, text, cancellationToken)
            : await TestFromMemoryAsync(command, recipient, text, cancellationToken);
    }

    private async Task<TestBotResult> TestSavedAsync(Guid id, string recipient, string text, CancellationToken cancellationToken)
    {
        var configuration = await configurations.GetForRuntimeAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Configuration {id} does not exist.");
        if (configuration.ChannelType is not (ChannelType.Chat or ChannelType.Telegram))
            throw new ValidationException(new Dictionary<string, string[]> { ["ConfigurationId"] = ["This is not a chat configuration."] });

        var result = await RunAsync(configuration, recipient, text, cancellationToken);
        await configurations.RecordTestResultAsync(id, result.Success ? TestStatus.Success : TestStatus.Failed, cancellationToken);
        return result with { UsedSavedConfiguration = true, ConfigurationId = id };
    }

    private async Task<TestBotResult> TestFromMemoryAsync(TestBotCommand command, string recipient, string text, CancellationToken cancellationToken)
    {
        var provider = command.ProviderType
            ?? throw new ValidationException(new Dictionary<string, string[]> { ["ProviderType"] = ["Give the provider to test, or the id of a saved configuration."] });

        // The same rules as saving: the right provider for the channel, no secret inside the settings, a token or address of the right shape.
        var settingsJson = string.IsNullOrWhiteSpace(command.SettingsJson) ? "{}" : command.SettingsJson;
        var errors = ChannelConfigurationValidator.Validate(new SaveChannelConfigurationCommand(
            NotificationBotDispatcher.FallbackProject, ChannelType.Chat, provider, "inline test", settingsJson, command.Secret))
            .ToDictionary(e => e.Key, e => e.Value.ToList());

        var secret = string.IsNullOrEmpty(command.Secret) || SecretMask.IsMask(command.Secret) ? null : command.Secret;
        if (secret is null && ProviderCatalog.RequiresSecret(provider))
            errors["Secret"] = [$"Enter the {SecretName(provider)} to test (a saved one is never used for settings that are not saved)."];

        JsonElement settings = default;
        if (errors.Count == 0)
        {
            using var document = JsonDocument.Parse(settingsJson);
            settings = document.RootElement.Clone();
        }

        if (errors.Count > 0)
            throw new ValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

        var configuration = new ChannelRuntimeConfiguration
        {
            Id = Guid.Empty,
            ProjectCode = NotificationBotDispatcher.FallbackProject,
            ChannelType = ChannelType.Chat,
            ProviderType = provider,
            Name = "unsaved test",
            Version = 0,
            Settings = settings,
            Secret = secret,
        };

        var result = await RunAsync(configuration, recipient, text, cancellationToken);

        audit.Record("SendTest", "ChannelConfiguration", "unsaved", new { Provider = provider.ToString(), result.Success, Saved = false });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<TestBotResult> RunAsync(ChannelRuntimeConfiguration configuration, string recipient, string text, CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(p => p.Type == configuration.ProviderType);
        if (provider is null)
            return new TestBotResult(false, $"The {configuration.ProviderType} chat provider is not available.", 0, configuration.ProviderType, false, null);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outcome = await provider.TestConnectionAsync(configuration, recipient, text, cancellationToken);
            return new TestBotResult(outcome.Success, ErrorText.Clean(outcome.Message, configuration.Secret), outcome.LatencyMs, configuration.ProviderType, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The {Provider} chat test threw.", configuration.ProviderType);
            return new TestBotResult(false, ErrorText.Clean(ex.Message, configuration.Secret), (int)stopwatch.ElapsedMilliseconds, configuration.ProviderType, false, null);
        }
    }

    private static string SecretName(ProviderType provider) => provider switch
    {
        ProviderType.TelegramBot => "bot token",
        ProviderType.DiscordWebhook or ProviderType.SlackWebhook => "webhook address",
        _ => "secret",
    };
}
