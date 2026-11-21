using Andril.Common.Accounts;
using Andril.Common.Entities;
using Andril.Common.Accounts.Preferences;
using Andril.Common.Accounts.Eligibility;
using Andril.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public DbSet<Andril.Common.Accounts.Profiles.PrivateModuleProfile> PrivateProfiles => Set<Andril.Common.Accounts.Profiles.PrivateModuleProfile>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountLanguagePreference> LanguagePreferences => Set<AccountLanguagePreference>();
    public DbSet<AccountDemographics> Demographics => Set<AccountDemographics>();
    public DbSet<Andril.Common.Accounts.Policies.AccountPolicyRecord> PolicyRecords => Set<Andril.Common.Accounts.Policies.AccountPolicyRecord>();
    public DbSet<Andril.Common.Accounts.StarterModules.StarterMembership> StarterMemberships => Set<Andril.Common.Accounts.StarterModules.StarterMembership>();

    public DbSet<PrivateProfilePhoto> PrivateProfilePhotos => Set<PrivateProfilePhoto>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.ApplyConfiguration(new AccountLanguagePreferenceConfiguration());
        modelBuilder.ApplyConfiguration(new AccountDemographicsConfiguration());
        modelBuilder.ApplyConfiguration(new AccountPolicyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new StarterMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new PrivateModuleProfileConfiguration());
        modelBuilder.ApplyConfiguration(new PrivateProfilePhotoConfiguration());
    }
}
