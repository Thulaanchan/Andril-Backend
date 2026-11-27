using Andril.Common.Accounts;
using Andril.Economy.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class CoinWalletConfiguration : IEntityTypeConfiguration<CoinWallet>
{
    public void Configure(EntityTypeBuilder<CoinWallet> builder)
    {
        builder.ToTable("coin_wallets", EconomyDatabaseOptions.Schema, table =>
        {
            table.HasCheckConstraint("ck_coin_wallets_account_id_nonempty", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_coin_wallets_balance_nonnegative", "balance >= 0");
            table.HasCheckConstraint("ck_coin_wallets_timestamps", "updated_at_utc >= created_at_utc");
        });

        builder.HasKey(x => x.AccountId).HasName("pk_coin_wallets");

        builder.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.Balance).HasColumnName("balance").HasColumnType("bigint").IsRequired();
        builder.Property(x => x.ConcurrencyVersion).HasColumnName("concurrency_version").HasColumnType("integer").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasOne<Account>().WithOne().HasForeignKey<CoinWallet>(x => x.AccountId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_coin_wallets_accounts");
    }
}
