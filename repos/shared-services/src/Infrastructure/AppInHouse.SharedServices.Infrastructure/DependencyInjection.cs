using AppInHouse.SharedServices.Infrastructure.Realtime;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Infrastructure.Jobs;
using AppInHouse.SharedServices.Infrastructure.Webhooks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AppInHouse.SharedServices.Infrastructure.Storage;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Infrastructure.Notifications.Bots;
using AppInHouse.SharedServices.Infrastructure.Email;
using AppInHouse.SharedServices.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AppInHouse.SharedServices.Infrastructure;

/// <summary>Where the Data Protection key ring lives. Section <c>DataProtection</c>.</summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// The same name in every host that must read the same secrets (the API and, later, the web portal). Changing it makes
    /// every stored secret unreadable.
    /// </summary>
    public string ApplicationName { get; set; } = "AppInHouse.SharedServices";

    /// <summary>
    /// A folder for the key ring; relative paths are resolved against the host's content root. Empty means the framework default
    /// (the user profile), which is fine on a developer machine and wrong in a container: set it, and mount it as a volume.
    /// </summary>
    public string? KeysPath { get; set; }
}

public static class DependencyInjection
{
    /// <param name="contentRootPath">The host's content root, the base of a relative <c>DataProtection:KeysPath</c>.</param>
    public static IServiceCollection AddSharedServicesInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var options = configuration.GetSection(SecretProtectionOptions.SectionName).Get<SecretProtectionOptions>()
                      ?? new SecretProtectionOptions();

        var builder = services.AddDataProtection().SetApplicationName(options.ApplicationName);
        if (!string.IsNullOrWhiteSpace(options.KeysPath))
        {
            var directory = Directory.CreateDirectory(Path.GetFullPath(options.KeysPath, contentRootPath));
            builder.PersistKeysToFileSystem(directory);

            // Keys written to a folder are plain XML unless something encrypts them. On Windows DPAPI can (bound to the account
            // running the host); elsewhere protect the folder itself (permissions, volume) or add a certificate.
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        }

        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        AddEmailProviders(services, configuration);
        AddChatProviders(services, configuration);
        AddStorageProviders(services, configuration);
        AddPlatformInfrastructure(services, configuration);
        return services;
    }

    /// <summary>The email providers the dispatcher can route to: SMTP (MailKit), SendGrid (REST) and a mock. Host settings: section <c>Email</c>.</summary>
    private static void AddEmailProviders(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.AddSingleton<IHostAddressResolver, DnsHostAddressResolver>();
        services.AddSingleton<OutboundHostPolicy>();
        services.AddSingleton<ISmtpSender, MailKitSmtpSender>();

        services.AddSingleton<MockEmailProvider>();
        services.AddSingleton<IEmailProvider>(provider => provider.GetRequiredService<MockEmailProvider>());
        services.AddSingleton<IEmailProvider, MailKitSmtpEmailProvider>();

        services.AddHttpClient<SendGridEmailProvider>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<EmailOptions>>().Value;
            client.BaseAddress = new Uri(options.SendGridBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.SendGridTimeoutSeconds);
        });
        services.AddTransient<IEmailProvider>(provider => provider.GetRequiredService<SendGridEmailProvider>());
    }

    /// <summary>
    /// The chat providers the dispatcher can route to: Telegram, Discord, Slack and a mock. Host settings: section <c>Chat</c>.
    /// The requests of the real providers carry a secret in their address (the bot token, the webhook), so their HTTP clients log nothing
    /// (the framework would otherwise write every request address to the log) and follow no redirect.
    /// </summary>
    private static void AddChatProviders(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BotOptions>(configuration.GetSection(BotOptions.SectionName));
        services.AddSingleton<MockBotProvider>();
        services.AddSingleton<IBotProvider>(provider => provider.GetRequiredService<MockBotProvider>());

        services.AddHttpClient<TelegramBotProvider>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<BotOptions>>().Value;
                client.BaseAddress = new Uri(options.TelegramBaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<DiscordWebhookBotProvider>((provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<BotOptions>>().Value.TimeoutSeconds))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<SlackWebhookBotProvider>((provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<BotOptions>>().Value.TimeoutSeconds))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddTransient<IBotProvider>(provider => provider.GetRequiredService<TelegramBotProvider>());
        services.AddTransient<IBotProvider>(provider => provider.GetRequiredService<DiscordWebhookBotProvider>());
        services.AddTransient<IBotProvider>(provider => provider.GetRequiredService<SlackWebhookBotProvider>());
    }

    /// <summary>The storage providers the service can route to (local disk, S3, a mock) and the thumbnail service. Host settings: section <c>Storage</c>.</summary>
    private static void AddStorageProviders(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.AddSingleton<MockStorageProvider>();
        services.AddSingleton<IStorageProvider>(provider => provider.GetRequiredService<MockStorageProvider>());
        services.AddSingleton<IStorageProvider, LocalDiskStorageProvider>();
        services.AddSingleton<IS3ClientFactory>(provider => new S3ClientFactory(provider.GetRequiredService<IOptions<StorageOptions>>(), provider.GetService<Amazon.Runtime.HttpClientFactory>()));
        // S3 reaches its service through a policy with its own private-network switch (Storage:AllowPrivateNetworkHosts).
        services.AddSingleton<IStorageProvider>(provider => new S3StorageProvider(
            provider.GetRequiredService<IS3ClientFactory>(),
            new OutboundHostPolicy(() => provider.GetRequiredService<IOptions<StorageOptions>>().Value.AllowPrivateNetworkHosts, provider.GetRequiredService<IHostAddressResolver>()),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<S3StorageProvider>>()));
        services.AddSingleton(provider => new Application.Configurations.ConfigurationRules(provider.GetRequiredService<IOptions<StorageOptions>>().Value.AllowAbsolutePaths));
        services.AddSingleton<IImageThumbnailService, ImageThumbnailService>();
    }

    /// <summary>
    /// The job engine (channels, the worker of the five queues, the scheduler) and the webhook sender. Host settings: sections <c>Jobs</c> and
    /// <c>Webhooks</c>. The sender's HTTP client follows no redirect and logs nothing, because the address of a webhook can carry a token.
    /// </summary>
    private static void AddPlatformInfrastructure(IServiceCollection services, IConfiguration configuration)
    {
        var jobs = configuration.GetSection(JobSettings.SectionName).Get<JobSettings>() ?? new JobSettings();
        services.Replace(ServiceDescriptor.Singleton(jobs));
        services.AddSingleton<IJobChannels, JobChannels>();
        services.AddHostedService<BackgroundJobWorker>();
        services.AddHostedService<ScheduleWorker>();

        services.Configure<WebhookOptions>(configuration.GetSection(WebhookOptions.SectionName));
        services.AddHttpClient<IWebhookSender, HttpWebhookSender>((provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(provider.GetRequiredService<IOptions<WebhookOptions>>().Value.TimeoutSeconds, 1, 60)))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IInboundSecretProvider, ConfigurationInboundSecretProvider>();

        // Conversations: who is connected (the realtime push itself is the SignalR hub of the API host). Settings: section "Chat".
        services.Replace(ServiceDescriptor.Singleton(configuration.GetSection(ConversationSettings.SectionName).Get<ConversationSettings>() ?? new ConversationSettings()));
        services.AddSingleton<IUserPresenceTracker, UserPresenceTracker>();
    }
}
