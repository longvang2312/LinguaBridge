using LinguaBridge.Modules.AiTutor.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LinguaBridge.Infrastructure.Persistence.Configurations.AiTutor;

public class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("chat_messages", Schemas.Ai);

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.SenderType).HasConversion<string>().HasMaxLength(10);
        builder.Property(m => m.Content).IsRequired();

        builder.HasOne<ChatSession>()
            .WithMany()
            .HasForeignKey(m => m.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
