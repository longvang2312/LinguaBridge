using AppInHouse.SharedServices.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AppInHouse.SharedServices.Persistence.Configurations;

// Tables of the chat conversations: rooms, who is in them, and what was said.

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.ProjectCode).HasMaxLength(50).IsRequired();
        builder.Property(c => c.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.Title).HasMaxLength(Conversation.MaxTitleLength);
        builder.Property(c => c.DirectKey).HasMaxLength(2 * ChatIdentity.MaxUserIdLength + 1);
        builder.Property(c => c.CreatedBy).HasMaxLength(ChatIdentity.MaxUserIdLength).IsRequired();

        // The same pair of users cannot have two direct conversations in a project. A group has no key, and a null never equals another null.
        builder.HasIndex(c => new { c.ProjectCode, c.DirectKey }).IsUnique();
        builder.HasIndex(c => new { c.ProjectCode, c.LastMessageAt });

        builder.HasMany(c => c.Members).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> builder)
    {
        builder.ToTable("conversation_members");
        builder.HasKey(m => new { m.ConversationId, m.UserId });

        builder.Property(m => m.UserId).HasMaxLength(ChatIdentity.MaxUserIdLength).IsRequired();
        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(m => m.DisplayName).HasMaxLength(ChatIdentity.MaxNameLength);
        builder.Property(m => m.Email).HasMaxLength(200);
        builder.Property(m => m.ChatId).HasMaxLength(200);

        // "My conversations" starts from the user.
        builder.HasIndex(m => m.UserId);
    }
}

internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.ToTable("chat_messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.SenderId).HasMaxLength(ChatIdentity.MaxUserIdLength).IsRequired();
        builder.Property(m => m.SenderName).HasMaxLength(ChatIdentity.MaxNameLength).IsRequired();
        builder.Property(m => m.MessageType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(m => m.Content).IsRequired();

        // No foreign key to the stored file: deleting a file must not delete what was said around it.
        builder.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);

        // The history is read newest first, a page at a time, from a cursor.
        builder.HasIndex(m => new { m.ConversationId, m.CreatedAt });
        builder.HasIndex(m => m.CreatedAt);
    }
}
