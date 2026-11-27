using Andril.Economy.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class CoinTransactionRecordConfiguration : IEntityTypeConfiguration<CoinTransactionRecord>
{
    public void Configure(EntityTypeBuilder<CoinTransactionRecord> builder)
    {
        builder.ToTable("coin_transactions", EconomyDatabaseOptions.Schema, table =>
        {
            table.HasCheckConstraint("ck_coin_transactions_id_nonempty", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_coin_transactions_account_id_nonempty", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_coin_transactions_balance_after_nonnegative", "balance_after >= 0");
            table.HasCheckConstraint("ck_coin_transactions_idempotency_key_nonempty", "length(trim(idempotency_key)) > 0");
        });

        builder.HasKey(x => x.Id).HasName("pk_coin_transactions");

        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Amount).HasColumnName("amount").HasColumnType("bigint").IsRequired();
        builder.Property(x => x.BalanceAfter).HasColumnName("balance_after").HasColumnType("bigint").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").HasConversion<int>().HasColumnType("integer").IsRequired();
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(256).HasColumnType("character varying(256)").IsRequired();
        builder.Property(x => x.ReferenceId).HasColumnName("reference_id").HasMaxLength(128).HasColumnType("character varying(128)");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).HasColumnType("character varying(128)").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasIndex(x => new { x.AccountId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_coin_transactions_account_idempotency");
        builder.HasIndex(x => new { x.AccountId, x.CreatedAtUtc }).HasDatabaseName("ix_coin_transactions_account_created");
    }
}
