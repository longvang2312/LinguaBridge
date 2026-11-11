using System.Text.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.Application.Security;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Configurations;

/// <summary>
/// Checks a configuration before it is saved. The most important rule protects the secrets: the settings are stored readable, so a
/// password, token or key typed into them by mistake is refused instead of being kept in plain text.
/// </summary>
public static partial class ChannelConfigurationValidator
{
    public const int MaxSettingsBytes = 16 * 1024;
    private const int MaxDepth = 8;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,49}$")]
    private static partial Regex ProjectCodePattern();

    // Property names that mean "this is a secret".
    [GeneratedRegex("password|passwd|pwd|secret|token|api[_-]?key|private[_-]?key|credential|authorization", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    // Values that look like a secret whatever the property is called: a Telegram bot token, a SendGrid key, a PEM private key.
    [GeneratedRegex(@"^\d{6,12}:[A-Za-z0-9_-]{30,}$|^SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}$|-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex SecretLookingValue();

    /// <summary>Project codes are lower case: "linguabridge", "identity", "default".</summary>
    public static string NormalizeProjectCode(string? projectCode) => (projectCode ?? string.Empty).Trim().ToLowerInvariant();

    public static IReadOnlyDictionary<string, string[]> Validate(SaveChannelConfigurationCommand command)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
            list.Add(message);
        }

        if (!ProjectCodePattern().IsMatch(NormalizeProjectCode(command.ProjectCode)))
            Add(nameof(command.ProjectCode), "Use 1 to 50 lower-case letters, digits, '-' or '_', starting with a letter or digit.");

        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100) Add(nameof(command.Name), "The name is required and at most 100 characters.");

        if (!Enum.IsDefined(command.ChannelType)) Add(nameof(command.ChannelType), "Unknown channel.");
        else if (!Enum.IsDefined(command.ProviderType)) Add(nameof(command.ProviderType), "Unknown provider.");
        else if (!ProviderCatalog.Supports(command.ChannelType, command.ProviderType))
            Add(nameof(command.ProviderType), $"The {command.ProviderType} provider cannot serve the {command.ChannelType} channel.");

        ValidateSettings(command, Add);

        if (command.Secret is { Length: > 4096 }) Add(nameof(command.Secret), "The secret is too long.");

        // A bot token or a webhook address must have the right shape (and a webhook address must be on its platform's host): the service
        // sends requests to them. The message never repeats the value.
        if (!string.IsNullOrEmpty(command.Secret) && !SecretMask.IsMask(command.Secret) && Enum.IsDefined(command.ProviderType)
            && (Chat.ChatSecretRules.Check(command.ProviderType, command.Secret) ?? Storage.StorageSecretRules.Check(command.ProviderType, command.Secret)) is { } secretProblem)
            Add(nameof(command.Secret), secretProblem);

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }

    private static void ValidateSettings(SaveChannelConfigurationCommand command, Action<string, string> add)
    {
        const string field = nameof(SaveChannelConfigurationCommand.SettingsJson);
        var settingsJson = command.SettingsJson;
        if (string.IsNullOrWhiteSpace(settingsJson)) return;   // an empty settings object is allowed

        if (System.Text.Encoding.UTF8.GetByteCount(settingsJson) > MaxSettingsBytes)
        {
            add(field, $"The settings are larger than {MaxSettingsBytes / 1024} KB.");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson, new JsonDocumentOptions { MaxDepth = MaxDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                add(field, "The settings must be a JSON object.");
                return;
            }

            foreach (var problem in FindSecrets(document.RootElement, string.Empty))
                add(field, problem);

            // Saving a draft only checks the shape of the email settings; sending or testing needs them complete.
            if (command.ChannelType == ChannelType.Email && Enum.IsDefined(command.ProviderType))
                foreach (var problem in Email.EmailSettingsReader.Validate(command.ProviderType, document.RootElement, complete: false))
                    add(field, problem);

            // Likewise the chat settings (chat id, parse mode, username, avatar address, channel, emoji): shape only, none is required.
            if (command.ChannelType is ChannelType.Chat or ChannelType.Telegram)
                foreach (var problem in Chat.ChatSettingsReader.Validate(document.RootElement))
                    add(field, problem);

            // And the storage settings (folder, size limit, bucket, region, service address, access key id): shape only when saving a draft.
            if (command.ChannelType == ChannelType.Storage && Enum.IsDefined(command.ProviderType))
                foreach (var problem in Storage.StorageSettingsReader.Validate(command.ProviderType, document.RootElement, complete: false))
                    add(field, problem);
        }
        catch (JsonException)
        {
            add(field, "The settings are not valid JSON (or are nested deeper than 8 levels).");
        }
    }

    private static IEnumerable<string> FindSecrets(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    if (SensitiveName().IsMatch(property.Name))
                        yield return $"'{childPath}' looks like a secret; send it as the secret, not in the settings.";
                    foreach (var problem in FindSecrets(property.Value, childPath)) yield return problem;
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    foreach (var problem in FindSecrets(item, $"{path}[{index++}]")) yield return problem;
                break;
            case JsonValueKind.String when SecretLookingValue().IsMatch(element.GetString() ?? string.Empty):
                yield return $"The value of '{path}' looks like a secret; send it as the secret, not in the settings.";
                break;
        }
    }
}
