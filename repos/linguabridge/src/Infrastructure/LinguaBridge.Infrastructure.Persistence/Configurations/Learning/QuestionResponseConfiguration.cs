using LinguaBridge.Modules.Learning.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.Learning;

public class QuestionResponseConfiguration : IEntityTypeConfiguration<QuestionResponse>
{
    public void Configure(EntityTypeBuilder<QuestionResponse> builder)
    {
        builder.ToTable("question_responses", Schemas.Learning);

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
    }
}
