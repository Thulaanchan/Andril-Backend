using System.Net;
using System.Text.Json;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.IntegrationTests.Database;
using Andril.Persistence.Accounts;
using Andril.Persistence.Repositories.Common;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Lifecycle;

[Collection("ControlledLifecyclePostgreSQL")]
public sealed class LifecycleAuthorizationContractPersistenceTests
{
    [LocalPostgresFact]
    public async Task DeactivationContractReplaysOnceAndStatusReadsLeaveOtherModulesUnchanged()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var store = CreateStore(db);
        var transitions = db.Service(store);
        await transitions.ExecuteAsync(db.Subject, new("learning", Guid.NewGuid(), false), Guid.NewGuid());
        await transitions.ExecuteAsync(db.Subject, new("hobbies", Guid.NewGuid(), false), Guid.NewGuid());
        await using var accounts = db.Open();
        var actor = new ConfirmedActor(db.Subject);
        var api = new ControlledLifecycleApiContracts(actor, actor, new AccountRepository(accounts),
            transitions, new(actor, store));

        using var body = JsonDocument.Parse("""{"expectedRevision":1}""");
        string[] keys = [Guid.NewGuid().ToString("D")];
        var result = await api.DeactivateAsync("learning", keys, body.RootElement, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal("inactive", result.Body?.State);
        Assert.Equal(2L, result.Body?.Revision);
        Assert.Equal(result, await api.DeactivateAsync("learning", keys, body.RootElement, Guid.NewGuid()));

        var status = await api.ReadStatusesAsync();
        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.Equal(new LifecycleModuleStatus("inactive", 2), status.Body?.Learning);
        Assert.Equal(new LifecycleModuleStatus("active", 1), status.Body?.Hobbies);
        Assert.Equal(2L, await db.Count("account_module_lifecycles"));
        Assert.Equal(3L, await db.Count("account_module_lifecycle_receipts"));
        Assert.Equal(3L, await db.Count("account_module_lifecycle_audit"));
    }

    [LocalPostgresFact]
    public async Task FreshInactiveEvidenceDeniesReadOfRetainedOwnedProfile()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var store = CreateStore(db);
        var transitions = db.Service(store);
        await transitions.ExecuteAsync(db.Subject, new("learning", Guid.NewGuid(), false), Guid.NewGuid());
        var profile = PrivateModuleProfile.Create(db.AccountId, "learning", DateTimeOffset.UtcNow);
        await using var owner = db.Open();
        owner.PrivateProfiles.Add(profile);
        await owner.SaveChangesAsync();

        var actor = new ConfirmedActor(db.Subject);
        var scope = new ModuleMembershipScope(db.AccountId, "learning");
        var authorization = new LifecycleProfileAuthorization(actor, actor, TimeProvider.System);
        var current = await store.ReadNavigationAsync(db.Subject, "learning", default);
        var purpose = new PrivateProfilePurposeEvidence(scope, profile.Id, PrivateProfileOperation.Read,
            1, current.Evidence.CurrentEvidenceVersion, ModuleAuthorizationOutcome.Satisfied);
        var evidence = new LifecycleProfileEvidence(current.Evidence, current.Membership, true,
            purpose, current.Evidence.EvaluatedAtUtc.AddMinutes(1));
        Assert.Equal(ModuleAuthorizationOutcome.Satisfied,
            authorization.Decide(scope, PrivateProfileOperation.Read, profile, evidence));

        await transitions.ExecuteAsync(db.Subject, new("learning", Guid.NewGuid(), false, 1,
            ModuleLifecycleOperation.Deactivate, scope), Guid.NewGuid());
        var inactive = await store.ReadNavigationAsync(db.Subject, "learning", default);
        var retained = await owner.PrivateProfiles.AsNoTracking().SingleAsync();
        Assert.Equal(profile.Id, retained.Id);
        Assert.Equal(ModuleAuthorizationOutcome.Denied, authorization.Decide(scope, PrivateProfileOperation.Read, retained,
            evidence with { Eligibility = inactive.Evidence, Membership = inactive.Membership }));
    }

    private static ControlledModuleLifecycleStore CreateStore(ControlledLifecycleDatabase db) =>
        new(db.RuntimeConnection, db.Authority, db.Authority, db.Authority, true, db.DeactivationAuthority);

    private sealed class ConfirmedActor(Guid subject) : ICurrentUser, IOnboardingIdentity
    {
        public Guid? UserId => subject;
        public bool IsAuthenticated => true;
        public bool IsConfirmedFor(Guid candidate) => candidate == subject;
    }
}
