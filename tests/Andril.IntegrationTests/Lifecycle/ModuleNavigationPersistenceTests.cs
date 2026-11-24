using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.IntegrationTests.Database;
using Andril.Persistence.Accounts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Lifecycle;

[Collection("ControlledLifecyclePostgreSQL")]
public sealed class ModuleNavigationPersistenceTests
{
    [LocalPostgresFact]
    [Trait("PartC", "M2-P3C-C-QA-08")]
    [Trait("PartC", "M2-P3C-C-QA-09")]
    [Trait("PartC", "M2-P3C-C-QA-10")]
    public async Task ScopedStatusReadsLeaveMembershipJournalsStarterAndProfilesUnchanged()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var store = CreateStore(db);
        var service = new ModuleNavigationService(new CurrentUser(db.Subject), store);
        var foreign = new ModuleNavigationService(new CurrentUser(Guid.NewGuid()), store);
        await db.Service(store).ExecuteAsync(db.Subject, new("learning", Guid.NewGuid(), false), Guid.NewGuid());

        Guid profileId;
        await using (var owner = db.Open())
        {
            var profile = PrivateModuleProfile.Create(db.AccountId, "learning", DateTimeOffset.UtcNow);
            profileId = profile.Id;
            owner.PrivateProfiles.Add(profile);
            owner.StarterMemberships.Add(StarterMembership.Create(db.AccountId, "learning", false, DateTimeOffset.UtcNow));
            await owner.SaveChangesAsync();
        }

        Assert.Equal(new(ModuleNavigationState.Active, "learning", 1), await service.ReadAsync("learning"));
        Assert.Equal(new(ModuleNavigationState.NeverActivated, "hobbies"), await service.ReadAsync("hobbies"));
        Assert.Equal(new(ModuleNavigationState.Denied), await foreign.ReadAsync("learning"));
        Assert.Equal(new(ModuleNavigationState.Active, "learning", 1), await service.ReadAsync("learning"));

        Assert.Equal(1, await db.Count("account_module_lifecycles"));
        Assert.Equal(1, await db.Count("account_module_lifecycle_receipts"));
        Assert.Equal(1, await db.Count("account_module_lifecycle_audit"));
        await using var verification = db.Open();
        Assert.Equal(profileId, (await verification.PrivateProfiles.SingleAsync()).Id);
        Assert.Equal("learning", (await verification.StarterMemberships.SingleAsync()).Module);
    }

    [LocalPostgresFact]
    [Trait("PartC", "M2-P3C-C-QA-12")]
    [Trait("PartC", "M2-P3C-C-QA-13")]
    public async Task ReadRacingDeactivationIsAConsistentSnapshotAndFreshReadSeesInactive()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var writer = CreateStore(db);
        await db.Service(writer).ExecuteAsync(db.Subject, new("learning", Guid.NewGuid(), false), Guid.NewGuid());
        var authority = new GatedAuthority(db.Authority);
        var reader = new ModuleNavigationService(new CurrentUser(db.Subject), CreateStore(db, authority));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = reader.ReadAsync("learning", deadline.Token);

        try
        {
            await authority.Entered.Task.WaitAsync(deadline.Token);
            await db.Service(writer).ExecuteAsync(db.Subject,
                new("learning", Guid.NewGuid(), false, 1, ModuleLifecycleOperation.Deactivate, new(db.AccountId, "learning")),
                Guid.NewGuid(), deadline.Token);
        }
        finally
        {
            authority.Release.TrySetResult();
            await pending;
        }

        Assert.Equal(new(ModuleNavigationState.Active, "learning", 1), await pending);
        Assert.Equal(new(ModuleNavigationState.Inactive, "learning", 2), await reader.ReadAsync("learning"));
        Assert.Equal(1, await db.Count("account_module_lifecycles"));
        Assert.Equal(2, await db.Count("account_module_lifecycle_receipts"));
        Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
    }

    private static ControlledModuleLifecycleStore CreateStore(ControlledLifecycleDatabase db, IControlledLifecycleAuthority? authority = null)
    {
        var settings = new NpgsqlConnectionStringBuilder(db.RuntimeConnection) { MaxPoolSize = 4 };
        return new(settings.ConnectionString, db.Authority, db.Authority, authority ?? db.Authority, true, db.DeactivationAuthority);
    }

    private sealed class CurrentUser(Guid subject) : ICurrentUser
    {
        public Guid? UserId => subject;
        public bool IsAuthenticated => true;
    }

    private sealed class GatedAuthority(IControlledLifecycleAuthority inner) : IControlledLifecycleAuthority
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LifecycleAuthorityState> ReadAsync(Guid subject, Guid account, string module, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.ReadAsync(subject, account, module, cancellationToken);
        }
    }
}
