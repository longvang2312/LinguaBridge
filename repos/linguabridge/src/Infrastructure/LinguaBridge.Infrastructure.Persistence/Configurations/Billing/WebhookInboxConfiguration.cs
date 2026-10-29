using LinguaBridge.Modules.Billing.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Billing;

public class WebhookInboxConfiguration : IEntityTypeConfiguration<WebhookInbox>
{
    public void Configure(EntityTypeBuilder<WebhookInbox> builder)
    {
        builder.ToTable("webhook_inbox", Schemas.Billing);

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.Provider).HasMaxLength(WebhookInbox.ProviderMaxLength).IsRequired();
        builder.Property(w => w.EventId).HasMaxLength(WebhookInbox.EventIdMaxLength).IsRequired();
        builder.Property(w => w.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(20);

        // Idempotency: a redelivered gateway event violates this instead of being applied twice.
        builder.HasIndex(w => new { w.Provider, w.EventId }).IsUnique().HasDatabaseName("uq_provider_event");
    }
}
