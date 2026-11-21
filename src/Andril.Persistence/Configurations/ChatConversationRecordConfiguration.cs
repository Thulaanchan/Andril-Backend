using Andril.Persistence.Repositories.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

/// <summary>Candidate, undeployed mapping used only by an isolated test model.</summary>
internal sealed class ChatConversationRecordConfiguration : IEntityTypeConfiguration<ChatConversationRecord>
{
    public void Configure(EntityTypeBuilder<ChatConversationRecord> builder)
    {
        builder.ToTable("conversations", "andril_chat", table =>
            table.HasCheckConstraint("ck_chat_conversation_module", "module IN ('learning', 'hobbies', 'social')"));
        builder.HasKey(record => new { record.Module, record.ConversationId })
            .HasName("pk_chat_conversations");

        builder.Property(record => record.Module)
            .HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16)
            .HasConversion(new ChatModuleValueConverter()).IsRequired();
        builder.Property(record => record.ConversationId)
            .HasColumnName("conversation_id").HasColumnType("uuid").ValueGeneratedNever().IsRequired();
    }
}
