using LinguaBridge.Infrastructure.Persistence.Converters;
using LinguaBridge.Modules.AiTutor.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.AiTutor;

public class ChatSessionConfiguration : IEntityTypeConfiguration<ChatSession>
{
    public void Configure(EntityTypeBuilder<ChatSession> builder)
    {
        builder.ToTable("chat_sessions", Schemas.Ai);

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.TargetLanguage).HasConversion<TargetLanguageConverter>().HasMaxLength(TargetLanguageConverter.MaxLength);
        builder.Property(s => s.ScenarioCode).HasMaxLength(ChatSession.ScenarioCodeMaxLength).IsRequired();
        builder.Property(s => s.Title).HasMaxLength(ChatSession.TitleMaxLength).IsRequired();
    }
}
