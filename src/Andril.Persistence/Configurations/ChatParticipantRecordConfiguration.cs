using Andril.Persistence.Repositories.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

/// <summary>
/// Candidate, undeployed Chat-only relationships. No M2 table or authorization grant
/// is mapped. NoAction avoids introducing an unapproved deletion cascade.
/// </summary>
internal sealed class ChatParticipantRecordConfiguration : IEntityTypeConfiguration<ChatParticipantRecord>
{
    public void Configure(EntityTypeBuilder<ChatParticipantRecord> builder)
    {
        builder.ToTable("conversation_participants", "andril_chat", table =>
            table.HasCheckConstraint("ck_chat_participant_module", "module IN ('learning', 'hobbies', 'social')"));
        builder.HasKey(record => new { record.Module, record.ConversationId, record.ModuleProfileId })
            .HasName("pk_chat_conversation_participants");

        builder.Property(record => record.Module)
            .HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16)
            .HasConversion(new ChatModuleValueConverter()).IsRequired();
        builder.Property(record => record.ConversationId)
            .HasColumnName("conversation_id").HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(record => record.ModuleProfileId)
            .HasColumnName("module_profile_id").HasColumnType("uuid").ValueGeneratedNever().IsRequired();

        builder.HasOne<ChatConversationRecord>().WithMany()
            .HasForeignKey(record => new { record.Module, record.ConversationId })
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_chat_participants_conversation");
        builder.HasIndex(record => new { record.Module, record.ModuleProfileId, record.ConversationId })
            .HasDatabaseName("ix_chat_participants_profile_conversation");
    }
}
