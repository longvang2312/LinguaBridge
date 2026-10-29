using LinguaBridge.Infrastructure.Persistence.Converters;
using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Content;

public class QuestionTranslationConfiguration : IEntityTypeConfiguration<QuestionTranslation>
{
    public void Configure(EntityTypeBuilder<QuestionTranslation> builder)
    {
        builder.ToTable("question_translations", Schemas.Content);

        builder.HasKey(t => new { t.QuestionId, t.BaseLanguage });

        builder.Property(t => t.BaseLanguage).HasConversion<BaseLanguageConverter>().HasMaxLength(BaseLanguageConverter.MaxLength);
        builder.Property(t => t.PromptTranslation).IsRequired();
    }
}
