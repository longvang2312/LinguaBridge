using AppInHouse.SharedServices.Application.ApiKeys;
using AppInHouse.SharedServices.Application.Audit;
using AppInHouse.SharedServices.Application.Notifications.Bots;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Conversations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Notifications;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Application.Webhooks;
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
        services.AddScoped<IApiKeyManager, ApiKeyManager>();
        services.AddScoped<IAuditLogReader, AuditLogReader>();

        // Email: the providers (SMTP, SendGrid, mock) are registered by the infrastructure layer.
        services.AddSingleton<EmailTemplateRenderer>();
        services.AddScoped<IEmailDispatcher, EmailDispatcher>();
        services.AddScoped<IEmailTestService, EmailTestService>();

        // Chat: the providers (Telegram, Discord, Slack, mock) are registered by the infrastructure layer.
        services.AddScoped<INotificationBotDispatcher, NotificationBotDispatcher>();
        services.AddScoped<INotificationBotTestService, NotificationBotTestService>();

        // Storage: the providers (local disk, S3, mock) and the thumbnail service are registered by the infrastructure layer.
        services.AddScoped<IStorageService, StorageService>();
        services.AddScoped<IStorageTestService, StorageTestService>();

        AddPlatformServices(services);

        // Conversations: the presence tracker and the realtime push come from the infrastructure layer and the host (without them nothing is pushed).
        services.TryAddSingleton(new ConversationSettings());
        services.TryAddSingleton<IChatRealtime, NoChatRealtime>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IOfflineNotificationHandler, OfflineNotificationHandler>();
        services.AddScoped<IChatMonitor, ChatMonitor>();
        return services;
    }
    /// <summary>Projects and quotas, notifications, the job engine, webhooks and metrics. Their stores and workers come from the other layers.</summary>
    private static void AddPlatformServices(IServiceCollection services)
    {
        services.TryAddSingleton(new JobSettings());

        services.AddScoped<IQuotaGuard, QuotaGuard>();
        services.AddScoped<IProjectManager, ProjectManager>();

        services.AddSingleton<INotificationChannelSender, ScopedNotificationChannelSender>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();

        services.AddScoped<IJobQueue, JobQueueService>();
        services.AddScoped<IJobHandler, EmailJobHandler>();
        services.AddScoped<IJobHandler, ChatJobHandler>();
        services.AddScoped<IJobHandler, MediaThumbnailJobHandler>();
        services.AddScoped<IJobHandler, OutboxJobHandler>();
        services.AddScoped<IJobHandler, WebhookJobHandler>();
        services.AddScoped<IThumbnailRebuilder, ThumbnailRebuilder>();

        services.AddScoped<IWebhookDeliverer, WebhookDeliverer>();
        services.AddScoped<IWebhookManager, WebhookManager>();
        services.AddScoped<IEventPublisher, OutboxEventPublisher>();
        services.AddScoped<IInboundWebhookReceiver, InboundWebhookReceiver>();

        services.AddSingleton<ScheduleState>();
        services.AddSingleton<ScheduleRunner>();
        services.AddSingleton<IScheduleService>(provider => provider.GetRequiredService<ScheduleRunner>());
        services.AddScoped<IScheduledTask, LogRetentionTask>();
        services.AddScoped<IScheduledTask, JobHousekeepingTask>();
        services.AddScoped<IScheduledTask, MetricsAggregationTask>();

        services.AddSingleton<MetricsSnapshotStore>();
        services.AddScoped<IMetricsCollector, MetricsCollector>();
    }
}
