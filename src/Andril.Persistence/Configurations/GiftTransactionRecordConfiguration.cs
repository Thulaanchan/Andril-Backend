using Andril.Economy.VirtualGifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class GiftTransactionRecordConfiguration : IEntityTypeConfiguration<GiftTransactionRecord>
{
    public void Configure(EntityTypeBuilder<GiftTransactionRecord> builder)
    {
        builder.ToTable("gift_transactions", EconomyDatabaseOptions.Schema, table =>
        {
            table.HasCheckConstraint("ck_gift_transactions_id_nonempty", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_gift_transactions_sender_nonempty", "sender_account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_gift_transactions_recipient_nonempty", "recipient_account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_gift_transactions_no_self_gift", "sender_account_id <> recipient_account_id");
            table.HasCheckConstraint("ck_gift_transactions_cost_positive", "coin_cost > 0");
        });

        builder.HasKey(x => x.Id).HasName("pk_gift_transactions");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.SenderAccountId).HasColumnName("sender_account_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.RecipientAccountId).HasColumnName("recipient_account_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.GiftId).HasColumnName("gift_id").HasMaxLength(64).HasColumnType("character varying(64)").IsRequired();
        builder.Property(x => x.CoinCost).HasColumnName("coin_cost").HasColumnType("bigint").IsRequired();
        builder.Property(x => x.Module).HasColumnName("module").HasMaxLength(32).HasColumnType("character varying(32)").IsRequired();
        builder.Property(x => x.Message).HasColumnName("message").HasMaxLength(500).HasColumnType("character varying(500)");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).HasColumnType("character varying(128)").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasIndex(x => new { x.SenderAccountId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_gift_transactions_sender_idempotency");
        builder.HasIndex(x => new { x.RecipientAccountId, x.CreatedAtUtc }).HasDatabaseName("ix_gift_transactions_recipient_created");
    }
}
