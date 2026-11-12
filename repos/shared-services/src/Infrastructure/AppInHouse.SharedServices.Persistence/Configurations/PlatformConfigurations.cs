using AppInHouse.SharedServices.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.SharedServices.Persistence.Configurations;

// Tables of the platform services (notifications, projects, webhooks, background jobs).

internal sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> builder)
    {
        builder.ToTable("notification_templates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Code).HasMaxLength(60).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.SubjectTemplate).HasMaxLength(NotificationTemplate.MaxSubjectLength).IsRequired();
        builder.Property(t => t.BodyTemplate).IsRequired();
        builder.Property(t => t.DefaultChannel).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(t => t.Code).IsUnique();
    }
}

internal sealed class NotificationLogConfiguration : IEntityTypeConfiguration<NotificationLog>
{
    public void Configure(EntityTypeBuilder<NotificationLog> builder)
    {
        builder.ToTable("notification_logs");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.ProjectCode).HasMaxLength(50).IsRequired();
        builder.Property(l => l.TemplateCode).HasMaxLength(60).IsRequired();
        builder.Property(l => l.Channels).HasMaxLength(60).IsRequired();
        builder.Property(l => l.Status).HasMaxLength(20).IsRequired();
        builder.Property(l => l.Recipient).HasMaxLength(200);

        builder.HasIndex(l => new { l.ProjectCode, l.CreatedAt });
        builder.HasIndex(l => l.CreatedAt);
    }
}

internal sealed class ProjectTenantConfiguration : IEntityTypeConfiguration<ProjectTenant>
{
    public void Configure(EntityTypeBuilder<ProjectTenant> builder)
    {
        builder.ToTable("project_tenants");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.ProjectCode).HasMaxLength(50).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(ProjectTenant.MaxNameLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(ProjectTenant.MaxDescriptionLength).IsRequired();
        builder.Property(p => p.Environment).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.HasIndex(p => p.ProjectCode).IsUnique();
    }
}

internal sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> builder)
    {
        builder.ToTable("webhook_subscriptions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ProjectCode).HasMaxLength(50).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(WebhookSubscription.MaxNameLength).IsRequired();
        builder.Property(s => s.TargetUrl).HasMaxLength(WebhookSubscription.MaxUrlLength).IsRequired();
        builder.Property(s => s.ProtectedSecret).IsRequired();
        builder.Property(s => s.SecretHint).HasMaxLength(8).IsRequired();
        builder.Property(s => s.SubscribedEvents).HasMaxLength(255).IsRequired();
        builder.Property(s => s.LastStatus).HasMaxLength(100);
        builder.Ignore(s => s.EventList);

        builder.HasIndex(s => s.ProjectCode);
    }
}

internal sealed class InboundWebhookLogConfiguration : IEntityTypeConfiguration<InboundWebhookLog>
{
    public void Configure(EntityTypeBuilder<InboundWebhookLog> builder)
    {
        builder.ToTable("inbound_webhook_logs");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Source).HasMaxLength(50).IsRequired();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(l => l.Error).HasMaxLength(200);

        builder.HasIndex(l => new { l.Source, l.CreatedAt });
    }
}

internal sealed class BackgroundJobRecordConfiguration : IEntityTypeConfiguration<BackgroundJobRecord>
{
    public void Configure(EntityTypeBuilder<BackgroundJobRecord> builder)
    {
        builder.ToTable("background_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.Queue).HasMaxLength(50).IsRequired();
        builder.Property(j => j.Type).HasMaxLength(50).IsRequired();
        builder.Property(j => j.PayloadJson).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(j => j.LastError).HasMaxLength(BackgroundJobRecord.MaxErrorLength);

        builder.HasIndex(j => new { j.Queue, j.Status });
        builder.HasIndex(j => j.CreatedAt);
    }
}
