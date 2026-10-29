using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Content;

public class QuestionConfiguration : IEntityTypeConfiguration<Question>
{
    public void Configure(EntityTypeBuilder<Question> builder)
    {
        builder.ToTable("questions", Schemas.Content);

        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();

        builder.Property(q => q.QuestionType).HasConversion<string>().HasMaxLength(30);
        builder.Property(q => q.AudioPromptUrl).HasMaxLength(Question.AudioPromptUrlMaxLength);
        builder.Property(q => q.TargetText).IsRequired();
        builder.Property(q => q.PinyinOrIpa).HasMaxLength(Question.PinyinOrIpaMaxLength);

        builder.HasOne<Lesson>()
            .WithMany()
            .HasForeignKey(q => q.LessonId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(q => q.Translations)
            .WithOne()
            .HasForeignKey(t => t.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(q => q.Options)
            .WithOne()
            .HasForeignKey(o => o.QuestionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
