using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Content;

public class QuestionOptionConfiguration : IEntityTypeConfiguration<QuestionOption>
{
    public void Configure(EntityTypeBuilder<QuestionOption> builder)
    {
        builder.ToTable("question_options", Schemas.Content);

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.OptionText).IsRequired();
        builder.Property(o => o.PinyinOrIpa).HasMaxLength(QuestionOption.PinyinOrIpaMaxLength);
    }
}
