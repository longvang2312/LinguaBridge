using LinguaBridge.Infrastructure.Persistence.Converters;
using LinguaBridge.Modules.Learning.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Learning;

public class UserVocabularyProgressConfiguration : IEntityTypeConfiguration<UserVocabularyProgress>
{
    public void Configure(EntityTypeBuilder<UserVocabularyProgress> builder)
    {
        builder.ToTable("user_vocabulary_progress", Schemas.Srs);

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.TargetLanguage).HasConversion<TargetLanguageConverter>().HasMaxLength(TargetLanguageConverter.MaxLength);
        builder.Property(p => p.State).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(p => new { p.UserId, p.VocabularyId }).IsUnique().HasDatabaseName("uq_user_vocab");

        // "Which cards are due for this learner today?" is the hot query of the review screen.
        builder.HasIndex(p => new { p.UserId, p.NextReviewDate }).HasDatabaseName("idx_srs_user_due");
    }
}
