using System.Diagnostics;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Security;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Application.Storage;

/// <summary>
/// "Test connection" for storage: writes a one-byte canary, reads it back and deletes it, with a configuration.
/// <list type="bullet">
/// <item>Without an id the provider, settings and secret in the request are used straight from memory. Nothing is stored and nothing is
/// activated: no configuration is created or changed (only an audit entry says who pressed the button).</item>
/// <item>With an id the saved version is tested, and its last test time and result are updated.</item>
/// </list>
/// The two are never mixed: a stored secret access key is not used with settings from the request.
/// </summary>
public sealed class StorageTestService(
    IChannelConfigManager configurations,
    IEnumerable<IStorageProvider> providers,
    IAuditLogWriter audit,
    IUnitOfWork unitOfWork,
    ILogger<StorageTestService> logger) : IStorageTestService
{
    public async Task<TestStorageResult> TestAsync(TestStorageCommand command, CancellationToken cancellationToken = default) =>
        command.ConfigurationId is { } id
            ? await TestSavedAsync(id, cancellationToken)
            : await TestFromMemoryAsync(command, cancellationToken);

    private async Task<TestStorageResult> TestSavedAsync(Guid id, CancellationToken cancellationToken)
    {
        var configuration = await configurations.GetForRuntimeAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Configuration {id} does not exist.");
        if (configuration.ChannelType != ChannelType.Storage)
            throw new ValidationException(new Dictionary<string, string[]> { ["ConfigurationId"] = ["This is not a storage configuration."] });

        var result = await RunAsync(configuration, cancellationToken);
        await configurations.RecordTestResultAsync(id, result.Success ? TestStatus.Success : TestStatus.Failed, cancellationToken);
        return result with { UsedSavedConfiguration = true, ConfigurationId = id };
    }

    private async Task<TestStorageResult> TestFromMemoryAsync(TestStorageCommand command, CancellationToken cancellationToken)
    {
        var provider = command.ProviderType
            ?? throw new ValidationException(new Dictionary<string, string[]> { ["ProviderType"] = ["Give the provider to test, or the id of a saved configuration."] });

        // The same rules as saving (right provider for the channel, no secret inside the settings, a key of the right shape), and then
        // the settings must be complete, because something is really going to be written.
        var settingsJson = string.IsNullOrWhiteSpace(command.SettingsJson) ? "{}" : command.SettingsJson;
        var errors = ChannelConfigurationValidator.Validate(new SaveChannelConfigurationCommand(
            StorageService.FallbackProject, ChannelType.Storage, provider, "inline test", settingsJson, command.Secret))
            .ToDictionary(e => e.Key, e => e.Value.ToList());

        var secret = string.IsNullOrEmpty(command.Secret) || SecretMask.IsMask(command.Secret) ? null : command.Secret;
        if (secret is null && ProviderCatalog.RequiresSecret(provider))
            errors["Secret"] = ["Enter the secret access key to test (a saved one is never used for settings that are not saved)."];

        JsonElement settings = default;
        if (errors.Count == 0)
        {
            using var document = JsonDocument.Parse(settingsJson);
            settings = document.RootElement.Clone();
            var problems = StorageSettingsReader.Validate(provider, settings, complete: true);
            if (problems.Count > 0) errors["SettingsJson"] = [.. problems];
        }

        if (errors.Count > 0)
            throw new ValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

        var configuration = new ChannelRuntimeConfiguration
        {
            Id = Guid.Empty,
            ProjectCode = StorageService.FallbackProject,
            ChannelType = ChannelType.Storage,
            ProviderType = provider,
            Name = "unsaved test",
            Version = 0,
            Settings = settings,
            Secret = secret,
        };

        var result = await RunAsync(configuration, cancellationToken);

        audit.Record("SendTest", "ChannelConfiguration", "unsaved", new { Provider = provider.ToString(), result.Success, Saved = false });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<TestStorageResult> RunAsync(ChannelRuntimeConfiguration configuration, CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(p => p.Type == configuration.ProviderType);
        if (provider is null)
            return new TestStorageResult(false, $"The {configuration.ProviderType} storage provider is not available.", 0, configuration.ProviderType, false, null);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outcome = await provider.TestConnectionAsync(configuration, cancellationToken);
            return new TestStorageResult(outcome.Success, ErrorText.Clean(outcome.Message, configuration.Secret), outcome.LatencyMs, configuration.ProviderType, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The {Provider} storage test threw.", configuration.ProviderType);
            return new TestStorageResult(false, ErrorText.Clean(ex.Message, configuration.Secret), (int)stopwatch.ElapsedMilliseconds, configuration.ProviderType, false, null);
        }
    }
}
