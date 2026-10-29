using LinguaBridge.Modules.AiTutor.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.AiTutor;

public class AiUsageLedgerConfiguration : IEntityTypeConfiguration<AiUsageLedger>
{
    public void Configure(EntityTypeBuilder<AiUsageLedger> builder)
    {
        builder.ToTable("ai_usage_ledger", Schemas.Ai);

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();

        builder.Property(l => l.TaskType).HasMaxLength(AiUsageLedger.TaskTypeMaxLength).IsRequired();
        builder.Property(l => l.ModelProvider).HasMaxLength(AiUsageLedger.ModelProviderMaxLength).IsRequired();
        builder.Property(l => l.EstimatedCostUsd).HasPrecision(10, 6);

        // Daily quota checks sum a learner's tokens over a time window.
        builder.HasIndex(l => new { l.UserId, l.CreatedAt }).HasDatabaseName("idx_ai_usage_user_daily");
    }
}
