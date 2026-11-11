namespace AppInHouse.SharedServices.Domain;

/// <summary>What a configuration is for.</summary>
public enum ChannelType
{
    Email,
    Telegram,
    Storage,
}

/// <summary>The concrete service behind a channel.</summary>
public enum ProviderType
{
    Smtp,
    SendGrid,
    Brevo,
    Mock,
    TelegramBot,
    LocalDisk,
    S3,
}

/// <summary>Life cycle of one version of a configuration: Draft, then Active, then Archived.</summary>
public enum ConfigurationStatus
{
    Draft,
    Active,
    Archived,
}

public enum TestStatus
{
    Untested,
    Success,
    Failed,
}

public enum DeliveryStatus
{
    Pending,
    Sent,
    Failed,
}

/// <summary>Which providers can serve which channel.</summary>
public static class ProviderCatalog
{
    private static readonly IReadOnlyDictionary<ChannelType, ProviderType[]> Map = new Dictionary<ChannelType, ProviderType[]>
    {
        [ChannelType.Email] = [ProviderType.Smtp, ProviderType.SendGrid, ProviderType.Brevo, ProviderType.Mock],
        [ChannelType.Telegram] = [ProviderType.TelegramBot, ProviderType.Mock],
        [ChannelType.Storage] = [ProviderType.LocalDisk, ProviderType.S3],
    };

    public static IReadOnlyList<ProviderType> For(ChannelType channel) => Map[channel];

    public static bool Supports(ChannelType channel, ProviderType provider) => Map[channel].Contains(provider);

    /// <summary>
    /// Whether the provider cannot work without a secret (an API key, a bot token). SMTP is not listed: a relay may accept mail
    /// without a login. Mock and LocalDisk need none.
    /// </summary>
    public static bool RequiresSecret(ProviderType provider) =>
        provider is ProviderType.SendGrid or ProviderType.Brevo or ProviderType.TelegramBot or ProviderType.S3;
}
