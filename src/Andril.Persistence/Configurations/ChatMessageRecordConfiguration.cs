using Andril.Persistence.Repositories.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

/// <summary>
/// Candidate, undeployed message mapping. Required columns and scoped relationships
/// are structural metadata, not complete domain validation or authorization.
/// No production context, migration or repository applies this configuration.
/// </summary>
internal sealed class ChatMessageRecordConfiguration : IEntityTypeConfiguration<ChatMessageRecord>
{
    public void Configure(EntityTypeBuilder<ChatMessageRecord> builder)
    {
        builder.ToTable("messages", "andril_chat", table =>
        {
            table.HasCheckConstraint("ck_chat_message_module", "module IN ('learning', 'hobbies', 'social')");
            // Current domain behavior rejects ALL non-null tones. A later approved
            // vocabulary change must retain Learning/Hobbies rejection explicitly.
            table.HasCheckConstraint("ck_chat_message_mood_tone_pending", "mood_tone IS NULL");
        });
        builder.HasKey(record => record.MessageId).HasName("pk_chat_messages");

        builder.Property(record => record.MessageId)
            .HasColumnName("message_id").HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(record => record.Module)
            .HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16)
            .HasConversion(new ChatModuleValueConverter()).IsRequired();
        builder.Property(record => record.ConversationId)
            .HasColumnName("conversation_id").HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(record => record.SenderModuleProfileId)
            .HasColumnName("sender_module_profile_id").HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(record => record.Text)
            .HasColumnName("text").HasColumnType("text").IsRequired();
        builder.Property(record => record.MoodTone)
            .HasColumnName("mood_tone").HasColumnType("text").IsRequired(false);
        builder.Property(record => record.CreatedAtUtc)
            .HasColumnName("created_at_utc").HasColumnType("timestamp with time zone")
            .ValueGeneratedNever().IsRequired();

        builder.HasOne<ChatConversationRecord>().WithMany()
            .HasForeignKey(record => new { record.Module, record.ConversationId })
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_chat_messages_conversation");
        builder.HasOne<ChatParticipantRecord>().WithMany()
            .HasForeignKey(record => new { record.Module, record.ConversationId, record.SenderModuleProfileId })
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_chat_messages_sender");
        builder.HasIndex(record => new { record.Module, record.ConversationId, record.CreatedAtUtc, record.MessageId })
            .IsDescending(false, false, true, true).HasDatabaseName("ix_chat_messages_history");
    }
}
