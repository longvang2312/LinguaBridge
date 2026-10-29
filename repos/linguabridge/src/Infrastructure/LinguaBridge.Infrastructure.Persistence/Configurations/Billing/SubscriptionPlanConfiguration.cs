using LinguaBridge.Modules.Billing.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Billing;

public class SubscriptionPlanConfiguration : IEntityTypeConfiguration<SubscriptionPlan>
{
    public void Configure(EntityTypeBuilder<SubscriptionPlan> builder)
    {
        builder.ToTable("subscription_plans", Schemas.Billing);

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasMaxLength(SubscriptionPlan.IdMaxLength).ValueGeneratedNever();

        builder.Property(p => p.Name).HasMaxLength(SubscriptionPlan.NameMaxLength).IsRequired();
        builder.Property(p => p.PriceVnd).HasPrecision(18, 0);
    }
}
