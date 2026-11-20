using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.StarterModules;
using Andril.Persistence.Repositories.Common;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class OnboardingResumePersistenceTests
{
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
