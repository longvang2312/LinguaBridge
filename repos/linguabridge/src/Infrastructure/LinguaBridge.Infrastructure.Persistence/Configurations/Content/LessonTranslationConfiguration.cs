using LinguaBridge.Infrastructure.Persistence.Converters;
using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Content;

public class LessonTranslationConfiguration : IEntityTypeConfiguration<LessonTranslation>
{
    public void Configure(EntityTypeBuilder<LessonTranslation> builder)
    {
        builder.ToTable("lesson_translations", Schemas.Content);

        builder.HasKey(t => new { t.LessonId, t.BaseLanguage });

        builder.Property(t => t.BaseLanguage).HasConversion<BaseLanguageConverter>().HasMaxLength(BaseLanguageConverter.MaxLength);
        builder.Property(t => t.Title).HasMaxLength(LessonTranslation.TitleMaxLength).IsRequired();
    }
}
