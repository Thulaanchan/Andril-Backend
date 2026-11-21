using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.StarterModules;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.Repositories.Common;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class OnboardingResumePersistenceTests
{
    [LocalPostgresFact]
    public async Task SocialCompletionResumesFromFreshContexts_AndConcurrentNameClaimsRemainUnique()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog();
        var owners = new[] { Account.Create(Guid.NewGuid(), now), Account.Create(Guid.NewGuid(), now) };
        await using (var seed = database.Open())
        {
            seed.Accounts.AddRange(owners);
            foreach (var account in owners)
            {
                seed.Demographics.Add(AccountDemographics.Create(account.Id, new(2000, 1, 1), "other", now));
                seed.PolicyRecords.AddRange(AccountPolicyRecord.Create(account.Id, PolicyActions.TermsAgreed, "v1", now),
                    AccountPolicyRecord.Create(account.Id, PolicyActions.PrivacyAcknowledged, "v1", now));
                seed.StarterMemberships.Add(StarterMembership.Create(account.Id, "social", true, now));
                seed.PrivateProfiles.Add(PrivateModuleProfile.Create(account.Id, "social", now));
            }
            await seed.SaveChangesAsync();
        }
        async Task<OnboardingStatus> Read(Account account)
        {
            await using var db = database.Open();
            var user = new User(account.SupabaseUserId);
            var accounts = new AccountRepository(db);
            var profiles = new PrivateProfileService(user, user, accounts, new PrivateProfileRepository(db), catalog, TimeProvider.System);
            return await new OnboardingStatusService(user, user, accounts,
                new StarterModuleService(user, accounts, new StarterModuleRepository(db), catalog, TimeProvider.System),
                new PrivateProfileCompletionReader(profiles)).GetAsync();
        }
        Assert.False((await Read(owners[0])).Progress.PrivateProfileComplete);
        var claims = await Task.WhenAll(owners.Select(async account =>
        {
            await using var db = database.Open();
            var user = new User(account.SupabaseUserId);
            var profiles = new PrivateProfileService(user, user, new AccountRepository(db), new PrivateProfileRepository(db), catalog, TimeProvider.System);
            try
            {
                await profiles.UpdateAsync("social", new("Social Person", null, "private"));
                return account;
            }
            catch (ProfileNameConflictException) { return null; }
        }));
        var winner = Assert.Single(claims, account => account is not null)!;
        var loser = owners.Single(account => account.Id != winner.Id);
        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Read(winner)));
        Assert.All(reads, status =>
        {
            Assert.True(status.Progress.PrivateProfileComplete);
            Assert.False(status.OnboardingComplete);
            Assert.False(status.ModuleHomeAuthorized);
            Assert.Null(status.BlockingDependency);
        });
        Assert.False((await Read(loser)).Progress.PrivateProfileComplete);
        catalog.Version = "v2";
        Assert.False((await Read(winner)).Progress.PrivateProfileComplete);
        await using var check = database.Open();
        Assert.Equal(1, await check.PrivateProfiles.CountAsync(x => x.CanonicalDisplayName == "social person"));
    }

    [LocalPostgresFact]
    public async Task FreshContextsResumePersistedProgress_AndIsolateAccounts()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var subject = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog();
        async Task<OnboardingStatus> Read(Guid id)
        {
            await using var db = database.Open();
            var user = new User(id);
            var accounts = new AccountRepository(db);
            return await new OnboardingStatusService(user, user, accounts,
                new StarterModuleService(user, accounts, new StarterModuleRepository(db), catalog, TimeProvider.System)).GetAsync();
        }
        Assert.Equal("initialize_account", (await Read(subject)).NextStep);
        Guid accountId;
        await using (var db = database.Open())
            accountId = (await new AccountRepository(db).GetOrCreateAsync(subject, now)).Id;
        Assert.Equal("complete_demographics", (await Read(subject)).NextStep);
        await using (var db = database.Open())
        {
            db.Demographics.Add(AccountDemographics.Create(accountId, new(2000, 1, 1), "other", now));
            await db.SaveChangesAsync();
        }
        Assert.Equal("review_policies", (await Read(subject)).NextStep);
        await using (var db = database.Open())
        {
            db.PolicyRecords.AddRange(AccountPolicyRecord.Create(accountId, PolicyActions.TermsAgreed, "v1", now),
                AccountPolicyRecord.Create(accountId, PolicyActions.PrivacyAcknowledged, "v1", now));
            await db.SaveChangesAsync();
        }
        Assert.Equal("select_starter", (await Read(subject)).NextStep);
        await using (var db = database.Open())
        {
            var user = new User(subject);
            await new StarterModuleService(user, new AccountRepository(db), new StarterModuleRepository(db), catalog,
                TimeProvider.System).SelectAsync("hobbies", null);
        }
        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Read(subject)));
        Assert.All(reads, status =>
        {
            Assert.Equal("profile_setup", status.NextStep);
            Assert.Equal("hobbies", status.SelectedModule);
            Assert.False(status.OnboardingComplete);
            Assert.False(status.ModuleHomeAuthorized);
        });
        Assert.Equal("initialize_account", (await Read(Guid.NewGuid())).NextStep);
        catalog.Version = "v2";
        Assert.Equal("review_policies", (await Read(subject)).NextStep);
        await using var check = database.Open();
        Assert.Equal(1, await check.Accounts.CountAsync());
        Assert.Equal(1, await check.StarterMemberships.CountAsync());
        Assert.Equal(2, await check.PolicyRecords.CountAsync());
    }

    private sealed class User(Guid subject) : ICurrentUser, IOnboardingIdentity
    {
        public Guid? UserId => subject;
        public bool IsAuthenticated => true;
        public bool IsConfirmedFor(Guid value) => value == subject;
    }
    private sealed class Catalog : IPolicyCatalogProvider
    {
        internal string Version = "v1";
        public PolicyCatalog GetCurrent() => new(new(Version, "https://policy.test/terms", "en"),
            new(Version, "https://policy.test/privacy", "en"), new(Version, "https://policy.test/community", "en"));
    }
}
