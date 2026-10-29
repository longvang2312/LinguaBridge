using LinguaBridge.Infrastructure.Persistence.Converters;
using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Content;

public class CourseTranslationConfiguration : IEntityTypeConfiguration<CourseTranslation>
{
    public void Configure(EntityTypeBuilder<CourseTranslation> builder)
    {
        builder.ToTable("course_translations", Schemas.Content);

        builder.HasKey(t => new { t.CourseId, t.BaseLanguage });

        builder.Property(t => t.BaseLanguage).HasConversion<BaseLanguageConverter>().HasMaxLength(BaseLanguageConverter.MaxLength);
        builder.Property(t => t.Title).HasMaxLength(CourseTranslation.TitleMaxLength).IsRequired();
    }
}
