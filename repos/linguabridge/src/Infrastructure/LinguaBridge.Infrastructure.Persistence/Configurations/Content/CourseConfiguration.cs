using LinguaBridge.Infrastructure.Persistence.Converters;
using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Content;

public class CourseConfiguration : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> builder)
    {
        builder.ToTable("courses", Schemas.Content);

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Code).HasMaxLength(Course.CodeMaxLength).IsRequired();
        builder.HasIndex(c => c.Code).IsUnique();

        builder.Property(c => c.TargetLanguage).HasConversion<TargetLanguageConverter>().HasMaxLength(TargetLanguageConverter.MaxLength);
        builder.Property(c => c.Level).HasConversion<LanguageLevelConverter>().HasMaxLength(LanguageLevelConverter.MaxLength);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.ThumbnailUrl).HasMaxLength(Course.ThumbnailUrlMaxLength);

        builder.HasMany(c => c.Translations)
            .WithOne()
            .HasForeignKey(t => t.CourseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
