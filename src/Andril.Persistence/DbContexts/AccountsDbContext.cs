using Andril.Common.Accounts;
using Andril.Common.Accounts.Preferences;
using Andril.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountLanguagePreference> LanguagePreferences => Set<AccountLanguagePreference>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.ApplyConfiguration(new AccountLanguagePreferenceConfiguration());
    }
}
