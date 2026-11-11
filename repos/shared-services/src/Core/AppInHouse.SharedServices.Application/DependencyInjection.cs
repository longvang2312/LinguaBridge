using AppInHouse.SharedServices.Application.Chat;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AppInHouse.SharedServices.Application;

public static class DependencyInjection
{
    /// <summary>The use cases. Persistence, the secret protector and the actor come from the other layers and the host.</summary>
    public static IServiceCollection AddSharedServicesApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IChannelConfigManager, ChannelConfigManager>();

        // Email: the providers (SMTP, SendGrid, mock) are registered by the infrastructure layer.
        services.AddSingleton<EmailTemplateRenderer>();
        services.AddScoped<IEmailDispatcher, EmailDispatcher>();
        services.AddScoped<IEmailTestService, EmailTestService>();

        // Chat: the providers (Telegram, Discord, Slack, mock) are registered by the infrastructure layer.
        services.AddScoped<IChatDispatcher, ChatDispatcher>();
        services.AddScoped<IChatTestService, ChatTestService>();

        // Storage: the providers (local disk, S3, mock) and the thumbnail service are registered by the infrastructure layer.
        services.AddScoped<IStorageService, StorageService>();
        services.AddScoped<IStorageTestService, StorageTestService>();
        return services;
    }
}
