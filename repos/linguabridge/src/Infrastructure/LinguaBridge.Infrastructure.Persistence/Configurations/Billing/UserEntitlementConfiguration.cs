using LinguaBridge.Modules.Billing.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Billing;

public class UserEntitlementConfiguration : IEntityTypeConfiguration<UserEntitlement>
{
    public void Configure(EntityTypeBuilder<UserEntitlement> builder)
    {
        builder.ToTable("user_entitlements", Schemas.Billing);

        builder.HasKey(e => e.UserId);
        builder.Property(e => e.UserId).ValueGeneratedNever();

        builder.Property(e => e.Tier).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.PlanId).HasMaxLength(SubscriptionPlan.IdMaxLength);

        // A plan that learners are on cannot be deleted (no ON DELETE clause in the design, i.e. NO ACTION).
        builder.HasOne<SubscriptionPlan>()
            .WithMany()
            .HasForeignKey(e => e.PlanId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
