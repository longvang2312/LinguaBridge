using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Api.Seeding;

/// <summary>
/// Development only (<c>SharedServices:Seeding:Enabled</c>): a key to call the API with and a few made-up configurations, so the
/// web portal has something to show on a fresh database. Safe to run again: it adds nothing once there is data. The secrets
/// here are fakes; real ones are typed in the portal and stored protected like any other.
/// </summary>
public static class DevelopmentSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("SharedServices:Seeding:Enabled")) return;

        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("DevelopmentSeeder");

        var devKey = configuration["SharedServices:Seeding:DevApiKey"];
        if (!string.IsNullOrWhiteSpace(devKey))
        {
            var keys = provider.GetRequiredService<IApiKeyRepository>();
            if (ApiKey.TryGetPrefix(devKey, out var prefix) && (await keys.FindByPrefixAsync(prefix, cancellationToken)).Count == 0)
            {
                await keys.AddAsync(ApiKey.FromPlainText(
                    "default", "Development key", [ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.EmailSend], devKey, expiresAt: null, TimeProvider.System.GetUtcNow()), cancellationToken);
                await provider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(cancellationToken);
                logger.LogInformation("Seeded the development API key ({Prefix}...).", prefix);
            }
        }

        var manager = provider.GetRequiredService<IChannelConfigManager>();
        if ((await manager.ListAsync(new ChannelConfigurationQuery(), cancellationToken)).Count > 0) return;

        // Email: the mock provider is active, so sending works on a machine with no mail server; a draft SMTP version waits to be tried.
        var mock = await manager.SaveAsync(new SaveChannelConfigurationCommand(
            "default", ChannelType.Email, ProviderType.Mock, "Default email",
            Json(new { senderEmail = "noreply@example.com", senderName = "AppInHouse (development)" })), cancellationToken);
        await manager.ActivateAsync(mock.Id, cancellationToken);
        await manager.SaveAsync(new SaveChannelConfigurationCommand(
            "default", ChannelType.Email, ProviderType.Smtp, "Default email",
            Json(new { host = "smtp.example.com", port = 587, useSsl = "StartTls", username = "noreply@example.com", senderEmail = "noreply@example.com", senderName = "AppInHouse" }),
            Secret: "dev-only-smtp-password"), cancellationToken);

        await manager.SaveAsync(new SaveChannelConfigurationCommand(
            "linguabridge", ChannelType.Telegram, ProviderType.TelegramBot, "Alerts bot",
            Json(new { defaultChatId = "-1000000000000", parseMode = "MarkdownV2" }),
            Secret: "000000000:dev-only-fake-bot-token-0000000000"), cancellationToken);

        var storage = await manager.SaveAsync(new SaveChannelConfigurationCommand(
            "default", ChannelType.Storage, ProviderType.LocalDisk, "Local uploads",
            Json(new { rootPath = "data/uploads", maxFileSizeMb = 10 })), cancellationToken);
        await manager.ActivateAsync(storage.Id, cancellationToken);

        logger.LogInformation("Seeded sample configurations (development only).");
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
