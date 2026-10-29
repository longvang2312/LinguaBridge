using LinguaBridge.Modules.Learning.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Learning;

public class ExerciseAttemptConfiguration : IEntityTypeConfiguration<ExerciseAttempt>
{
    public void Configure(EntityTypeBuilder<ExerciseAttempt> builder)
    {
        builder.ToTable("exercise_attempts", Schemas.Learning, table =>
            table.HasCheckConstraint("ck_exercise_attempts_score_percentage", "score_percentage BETWEEN 0 AND 100"));

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.HasMany(a => a.Responses)
            .WithOne()
            .HasForeignKey(r => r.AttemptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
