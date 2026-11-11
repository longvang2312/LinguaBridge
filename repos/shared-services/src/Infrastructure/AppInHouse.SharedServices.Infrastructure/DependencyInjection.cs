using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Chat;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Infrastructure.Chat;
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
        services.Configure<ChatOptions>(configuration.GetSection(ChatOptions.SectionName));
        services.AddSingleton<MockChatProvider>();
        services.AddSingleton<IChatProvider>(provider => provider.GetRequiredService<MockChatProvider>());

        services.AddHttpClient<TelegramBotChatProvider>((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<ChatOptions>>().Value;
                client.BaseAddress = new Uri(options.TelegramBaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<DiscordWebhookChatProvider>((provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<ChatOptions>>().Value.TimeoutSeconds))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<SlackWebhookChatProvider>((provider, client) =>
                client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<ChatOptions>>().Value.TimeoutSeconds))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        services.AddTransient<IChatProvider>(provider => provider.GetRequiredService<TelegramBotChatProvider>());
        services.AddTransient<IChatProvider>(provider => provider.GetRequiredService<DiscordWebhookChatProvider>());
        services.AddTransient<IChatProvider>(provider => provider.GetRequiredService<SlackWebhookChatProvider>());
    }
}
