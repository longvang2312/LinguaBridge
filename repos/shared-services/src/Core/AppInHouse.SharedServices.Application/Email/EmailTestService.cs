using System.Diagnostics;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Security;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Email;

/// <summary>
/// "Send Test": sends a short email with a configuration to see that it works.
/// <list type="bullet">
/// <item>Without an id the provider, settings and secret in the request are used straight from memory. Nothing is stored and nothing
/// is activated: no configuration is created or changed (only an audit entry says who pressed the button).</item>
/// <item>With an id the saved version is tested, and its last test time and result are updated.</item>
/// </list>
/// The two are never mixed: a stored secret is not used with settings from the request, otherwise anyone who can test could point the
/// stored login at a server of their own and read it there.
/// </summary>
public sealed class EmailTestService(
    IChannelConfigManager configurations,
    IEnumerable<IEmailProvider> providers,
    IAuditLogWriter audit,
    IUnitOfWork unitOfWork,
    ILogger<EmailTestService> logger) : IEmailTestService
{
    public async Task<TestEmailResult> TestAsync(TestEmailCommand command, CancellationToken cancellationToken = default)
    {
        if (!EmailSettingsReader.IsAddress(command.TestRecipient))
            throw new ValidationException(new Dictionary<string, string[]> { ["TestRecipient"] = ["Give one valid email address to send the test to."] });

        return command.ConfigurationId is { } id
            ? await TestSavedAsync(id, command.TestRecipient.Trim(), cancellationToken)
            : await TestFromMemoryAsync(command, cancellationToken);
    }

    private async Task<TestEmailResult> TestSavedAsync(Guid id, string recipient, CancellationToken cancellationToken)
    {
        var configuration = await configurations.GetForRuntimeAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Configuration {id} does not exist.");
        if (configuration.ChannelType != ChannelType.Email)
            throw new ValidationException(new Dictionary<string, string[]> { ["ConfigurationId"] = ["This is not an email configuration."] });

        var result = await RunAsync(configuration, recipient, cancellationToken);
        await configurations.RecordTestResultAsync(id, result.Success ? TestStatus.Success : TestStatus.Failed, cancellationToken);
        return result with { UsedSavedConfiguration = true, ConfigurationId = id };
    }

    private async Task<TestEmailResult> TestFromMemoryAsync(TestEmailCommand command, CancellationToken cancellationToken)
    {
        var provider = command.ProviderType
            ?? throw new ValidationException(new Dictionary<string, string[]> { ["ProviderType"] = ["Give the provider to test, or the id of a saved configuration."] });

        // The same rules as saving (right provider for the channel, no secret inside the settings) ...
        var settingsJson = string.IsNullOrWhiteSpace(command.SettingsJson) ? "{}" : command.SettingsJson;
        var errors = ChannelConfigurationValidator.Validate(new SaveChannelConfigurationCommand(
            EmailDispatcher.FallbackProject, ChannelType.Email, provider, "inline test", settingsJson, command.Secret))
            .ToDictionary(e => e.Key, e => e.Value.ToList());

        JsonElement settings = default;
        if (errors.Count == 0)
        {
            using var document = JsonDocument.Parse(settingsJson);
            settings = document.RootElement.Clone();

            // ... and the settings must be complete, because this really sends.
            var problems = EmailSettingsReader.Validate(provider, settings, complete: true);
            if (problems.Count > 0) errors["SettingsJson"] = [.. problems];
        }

        var secret = string.IsNullOrEmpty(command.Secret) || SecretMask.IsMask(command.Secret) ? null : command.Secret;
        if (secret is null && ProviderCatalog.RequiresSecret(provider))
            errors["Secret"] = [$"Enter the {SecretName(provider)} to test (a saved one is never used for settings that are not saved)."];

        if (errors.Count > 0)
            throw new ValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

        var configuration = new ChannelRuntimeConfiguration
        {
            Id = Guid.Empty,
            ProjectCode = EmailDispatcher.FallbackProject,
            ChannelType = ChannelType.Email,
            ProviderType = provider,
            Name = "unsaved test",
            Version = 0,
            Settings = settings,
            Secret = secret,
        };

        var result = await RunAsync(configuration, command.TestRecipient.Trim(), cancellationToken);

        audit.Record("SendTest", "ChannelConfiguration", "unsaved", new { Provider = provider.ToString(), result.Success, Saved = false });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<TestEmailResult> RunAsync(ChannelRuntimeConfiguration configuration, string recipient, CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(p => p.Type == configuration.ProviderType);
        if (provider is null)
            return new TestEmailResult(false, $"The {configuration.ProviderType} email provider is not available.", 0, configuration.ProviderType, false, null);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outcome = await provider.TestConnectionAsync(configuration, recipient, cancellationToken);
            return new TestEmailResult(
                outcome.Success, ErrorText.Clean(outcome.Message, configuration.Secret), outcome.LatencyMs, configuration.ProviderType, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The {Provider} email test threw.", configuration.ProviderType);
            return new TestEmailResult(
                false, ErrorText.Clean(ex.Message, configuration.Secret), (int)stopwatch.ElapsedMilliseconds, configuration.ProviderType, false, null);
        }
    }

    private static string SecretName(ProviderType provider) => provider switch
    {
        ProviderType.SendGrid or ProviderType.Brevo => "API key",
        ProviderType.TelegramBot => "bot token",
        _ => "secret",
    };
}
