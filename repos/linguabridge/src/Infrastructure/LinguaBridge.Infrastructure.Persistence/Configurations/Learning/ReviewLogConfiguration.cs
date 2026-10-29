using LinguaBridge.Modules.Learning.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Learning;

public class ReviewLogConfiguration : IEntityTypeConfiguration<ReviewLog>
{
    public void Configure(EntityTypeBuilder<ReviewLog> builder)
    {
        builder.ToTable("review_logs", Schemas.Srs);

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();

        // Stored as 1-4, as in the SM-2 rating scale.
        builder.Property(l => l.Rating).HasConversion<int>();

        builder.HasOne<UserVocabularyProgress>()
            .WithMany()
            .HasForeignKey(l => l.UserVocabularyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
