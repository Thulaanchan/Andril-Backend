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
    public DbSet<Andril.Common.Accounts.Privacy.UserPrivacySettings> PrivacySettings => Set<Andril.Common.Accounts.Privacy.UserPrivacySettings>();

    public DbSet<PrivateProfilePhoto> PrivateProfilePhotos => Set<PrivateProfilePhoto>();
    public DbSet<Andril.Common.Accounts.Profiles.ProfileLocationRecord> ProfileLocations => Set<Andril.Common.Accounts.Profiles.ProfileLocationRecord>();
    public DbSet<Andril.Common.Accounts.Profiles.AdditionalProfilePhotoRecord> AdditionalPhotos => Set<Andril.Common.Accounts.Profiles.AdditionalProfilePhotoRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.ApplyConfiguration(new AccountLanguagePreferenceConfiguration());
        modelBuilder.ApplyConfiguration(new AccountDemographicsConfiguration());
        modelBuilder.ApplyConfiguration(new AccountPolicyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new StarterMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new PrivateModuleProfileConfiguration());
        modelBuilder.ApplyConfiguration(new PrivateProfilePhotoConfiguration());
        modelBuilder.ApplyConfiguration(new ProfileLocationConfiguration());
        modelBuilder.ApplyConfiguration(new AdditionalProfilePhotoConfiguration());
        modelBuilder.ApplyConfiguration(new UserPrivacySettingsConfiguration());
        modelBuilder.ApplyConfiguration(new Andril.Persistence.Accounts.ModuleLifecycleConfiguration());
        modelBuilder.ApplyConfiguration(new Andril.Persistence.Accounts.ModuleLifecycleReceiptConfiguration());
        modelBuilder.ApplyConfiguration(new Andril.Persistence.Accounts.ModuleLifecycleAuditConfiguration());
        modelBuilder.ApplyConfiguration(new Andril.Persistence.Accounts.AccountOwnerStateConfiguration());
        modelBuilder.ApplyConfiguration(new Andril.Persistence.Accounts.AccountOwnerReceiptConfiguration());
        modelBuilder.ApplyConfiguration(new Andril.Persistence.Accounts.AccountOwnerAuditConfiguration());
    }
}
