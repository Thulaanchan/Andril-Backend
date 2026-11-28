using Andril.Common.Accounts;
using Andril.Economy.Premium;
using Andril.Economy.VirtualGifts;
using Andril.Economy.Wallet;
using Andril.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

public sealed class EconomyDbContext(DbContextOptions<EconomyDbContext> options) : DbContext(options)
{
    public DbSet<CoinWallet> CoinWallets => Set<CoinWallet>();
    public DbSet<CoinTransactionRecord> CoinTransactions => Set<CoinTransactionRecord>();
    public DbSet<GiftTransactionRecord> GiftTransactions => Set<GiftTransactionRecord>();
    public DbSet<EntitlementRecord> PremiumEntitlements => Set<EntitlementRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Accounts owns this principal. Economy references the existing table but
        // must never create or alter it through its own migration chain.
        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.Entity<Account>().ToTable("accounts", "andril_identity",
            table => table.ExcludeFromMigrations());

        modelBuilder.ApplyConfiguration(new CoinWalletConfiguration());
        modelBuilder.ApplyConfiguration(new CoinTransactionRecordConfiguration());
        modelBuilder.ApplyConfiguration(new GiftTransactionRecordConfiguration());
        modelBuilder.ApplyConfiguration(new EntitlementRecordConfiguration());
    }
}
