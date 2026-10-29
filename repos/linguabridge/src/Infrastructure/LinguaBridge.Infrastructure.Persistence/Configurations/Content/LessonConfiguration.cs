using LinguaBridge.Modules.Content.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Content;

public class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        builder.ToTable("lessons", Schemas.Content);

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();

        builder.Property(l => l.LessonType).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Course>()
            .WithMany()
            .HasForeignKey(l => l.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(l => l.Translations)
            .WithOne()
            .HasForeignKey(t => t.LessonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
