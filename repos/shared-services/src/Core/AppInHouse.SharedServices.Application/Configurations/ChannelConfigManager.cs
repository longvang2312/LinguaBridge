using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Security;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Configurations;

public sealed class ChannelConfigManager(
    IChannelConfigRepository repository,
    ISecretProtector secretProtector,
    IAuditLogWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IChannelConfigManager
{
    public async Task<ChannelConfigurationDto> SaveAsync(SaveChannelConfigurationCommand command, CancellationToken cancellationToken = default)
    {
        var errors = ChannelConfigurationValidator.Validate(command);
        if (errors.Count > 0) throw new ValidationException(errors);

        var projectCode = ChannelConfigurationValidator.NormalizeProjectCode(command.ProjectCode);
        var name = command.Name.Trim();
        var now = clock.GetUtcNow();

        // A new secret is protected right away; no secret (or the mask the form echoed) keeps the stored one.
        var typedSecret = !string.IsNullOrEmpty(command.Secret) && !SecretMask.IsMask(command.Secret);
        var encryptedSecret = typedSecret ? secretProtector.Protect(command.Secret!) : null;

        var series = await repository.GetSeriesAsync(projectCode, command.ChannelType, name, cancellationToken);

        // A new provider has its own credential: the secret of the previous provider is never reused for it, so it must be typed
        // (an SMTP password must not silently become a SendGrid API key).
        var previousProvider = series.Count == 0 ? (ProviderType?)null : series.OrderBy(v => v.Version).Last().ProviderType;
        if (previousProvider is { } before && before != command.ProviderType && !typedSecret && ProviderCatalog.RequiresSecret(command.ProviderType))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.Secret)] = [$"The provider changed from {before} to {command.ProviderType}: enter the secret for {command.ProviderType}. The previous secret is not reused."],
            });
        }

        ChannelConfiguration saved;
        string action;
        if (series.Count == 0)
        {
            saved = ChannelConfiguration.CreateFirstVersion(
                projectCode, command.ChannelType, command.ProviderType, name, command.SettingsJson, encryptedSecret, now);
            action = "CreateConfig";
        }
        else
        {
            saved = ChannelConfigurationVersioning.CreateNextVersion(series, command.ProviderType, command.SettingsJson, encryptedSecret, now);
            action = "CreateVersion";
        }

        await repository.AddAsync(saved, cancellationToken);
        audit.Record(action, "ChannelConfiguration", saved.Id.ToString(), new
        {
            saved.ProjectCode,
            ChannelType = saved.ChannelType.ToString(),
            ProviderType = saved.ProviderType.ToString(),
            saved.Name,
            saved.Version,
            SecretChanged = typedSecret,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return ToDto(saved);
    }

    public async Task<ChannelConfigurationDto> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var configuration = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Configuration {id} does not exist.");
        var series = await repository.GetSeriesAsync(configuration.ProjectCode, configuration.ChannelType, configuration.Name, cancellationToken);
        var previous = ChannelConfigurationVersioning.ActiveVersion(series)?.Version;

        var activated = ChannelConfigurationVersioning.Activate(series, configuration.Version, clock.GetUtcNow());

        audit.Record("ActivateVersion", "ChannelConfiguration", activated.Id.ToString(), new
        {
            activated.ProjectCode,
            ChannelType = activated.ChannelType.ToString(),
            activated.Name,
            activated.Version,
            PreviousActiveVersion = previous,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return ToDto(activated);
    }

    public async Task<ChannelConfigurationDto> RollbackAsync(RollbackConfigurationCommand command, CancellationToken cancellationToken = default)
    {
        var projectCode = ChannelConfigurationValidator.NormalizeProjectCode(command.ProjectCode);
        var name = command.Name?.Trim() ?? string.Empty;
        var series = await repository.GetSeriesAsync(projectCode, command.ChannelType, name, cancellationToken);
        if (series.Count == 0)
            throw new NotFoundException($"There is no {command.ChannelType} configuration '{name}' for project '{projectCode}'.");

        var from = ChannelConfigurationVersioning.ActiveVersion(series)?.Version;
        var restored = ChannelConfigurationVersioning.Rollback(series, command.TargetVersion, clock.GetUtcNow());

        audit.Record("RollbackVersion", "ChannelConfiguration", restored.Id.ToString(), new
        {
            restored.ProjectCode,
            ChannelType = restored.ChannelType.ToString(),
            restored.Name,
            RestoredVersion = restored.Version,
            FromVersion = from,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return ToDto(restored);
    }

    public async Task<ChannelConfigurationDto> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ToDto(await repository.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException($"Configuration {id} does not exist."));

    public async Task<IReadOnlyList<ChannelConfigurationDto>> ListAsync(ChannelConfigurationQuery query, CancellationToken cancellationToken = default)
    {
        var projectCode = string.IsNullOrWhiteSpace(query.ProjectCode) ? null : ChannelConfigurationValidator.NormalizeProjectCode(query.ProjectCode);
        var items = await repository.ListAsync(projectCode, query.ChannelType, query.Status, cancellationToken);

        return [.. items
            .OrderBy(c => c.ProjectCode, StringComparer.Ordinal)
            .ThenBy(c => c.ChannelType)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenByDescending(c => c.Version)
            .Select(ToDto)];
    }

    public async Task<ChannelRuntimeConfiguration?> GetForRuntimeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var configuration = await repository.GetByIdAsync(id, cancellationToken);
        return configuration is null ? null : ToRuntime(configuration);
    }

    public async Task RecordTestResultAsync(Guid id, TestStatus result, CancellationToken cancellationToken = default)
    {
        var configuration = await repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Configuration {id} does not exist.");

        configuration.RecordTest(result, clock.GetUtcNow());
        audit.Record("SendTest", "ChannelConfiguration", id.ToString(), new
        {
            configuration.ProjectCode,
            ChannelType = configuration.ChannelType.ToString(),
            configuration.Name,
            configuration.Version,
            Result = result.ToString(),
            Saved = true,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<ChannelRuntimeConfiguration?> GetActiveForRuntimeAsync(
        string projectCode, ChannelType channelType, string? name = null, CancellationToken cancellationToken = default)
    {
        var active = await repository.ListAsync(
            ChannelConfigurationValidator.NormalizeProjectCode(projectCode), channelType, ConfigurationStatus.Active, cancellationToken);

        var chosen = string.IsNullOrWhiteSpace(name)
            ? active.OrderByDescending(c => c.ActivatedAt).FirstOrDefault()
            : active.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.Ordinal));
        return chosen is null ? null : ToRuntime(chosen);
    }

    private ChannelRuntimeConfiguration ToRuntime(ChannelConfiguration chosen) => new()
    {
        Id = chosen.Id,
        ProjectCode = chosen.ProjectCode,
        ChannelType = chosen.ChannelType,
        ProviderType = chosen.ProviderType,
        Name = chosen.Name,
        Version = chosen.Version,
        Settings = ParseSettings(chosen.SettingsJson),
        Secret = chosen.HasSecret ? secretProtector.Unprotect(chosen.EncryptedSecret!) : null,
    };

    private static ChannelConfigurationDto ToDto(ChannelConfiguration c) => new(
        c.Id, c.ProjectCode, c.ChannelType, c.ProviderType, c.Name, c.Version, c.Status,
        ParseSettings(c.SettingsJson), c.HasSecret, SecretMask.For(c.HasSecret),
        c.LastTestStatus, c.LastTestedAt, c.ActivatedAt, c.CreatedAt, c.UpdatedAt);

    private static JsonElement ParseSettings(string settingsJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson);
        return document.RootElement.Clone();
    }
}
