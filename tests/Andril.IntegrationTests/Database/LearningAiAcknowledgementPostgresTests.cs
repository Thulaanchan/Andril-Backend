using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.API.Integrations.AI;
using Andril.Persistence.Repositories.AI;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningAiAcknowledgementPostgresTests
{
    private sealed class Policies : IAiPrivacyPolicyVersionSource
    {
        internal string? Version = "synthetic-notice-v1";
        public Task<string?> ReadCurrentVersionAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Version); }
    }

    [LocalPostgresFact]
    public async Task MissingAcknowledgementHasNoPositiveEvidenceAndGrantRoundTripsDurably()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var r = f.Acknowledgements(p); var a = f.Actor();
        var absent = await r.ReadAsync(a); Assert.Equal(AiAcknowledgementOutcome.Available, absent.Outcome); Assert.Null(absent.State);
        var saved = await r.AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null);
        Assert.Equal(AiAcknowledgementOutcome.Saved, saved.Outcome); Assert.True(saved.State!.Acknowledged);
        Assert.Equal(p.Version, saved.State.PolicyVersion); Assert.Equal(1, saved.State.Revision.Version);
        Assert.NotEqual(Guid.Empty, saved.State.Revision.Generation);
        Assert.Equal(saved.State, (await f.Acknowledgements(p).ReadAsync(a)).State);
        await using var db = await f.BoundOwner(); var row = Assert.Single(await db.Set<IsolatedAiAcknowledgementRow>().ToListAsync());
        Assert.Equal(a.UserId, row.AccountId); Assert.Equal(a.ModuleProfileId, row.ModuleProfileId); Assert.Equal(f.Time.Now, row.UpdatedAtUtc);
    }

    [LocalPostgresFact]
    public async Task ClientPolicyVersionCannotAuthorizeObsoleteOrUnavailablePolicy()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var r = f.Acknowledgements(p); var a = f.Actor();
        Assert.Equal(AiAcknowledgementOutcome.Conflict, (await r.AcknowledgeAsync(a, "obsolete", Guid.NewGuid(), null)).Outcome);
        p.Version = null;
        Assert.Equal(AiAcknowledgementOutcome.Unavailable, (await r.AcknowledgeAsync(a, "synthetic-notice-v1", Guid.NewGuid(), null)).Outcome);
        Assert.Null((await r.ReadAsync(a)).State);
    }

    [LocalPostgresFact]
    public async Task RevocationIsDurableAndDoesNotDependOnAvailablePolicyAuthority()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var r = f.Acknowledgements(p); var a = f.Actor();
        var granted = await r.AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null); p.Version = null;
        var revoked = await r.RevokeAsync(a, Guid.NewGuid(), granted.State!.Revision);
        Assert.Equal(AiAcknowledgementOutcome.Saved, revoked.Outcome); Assert.False(revoked.State!.Acknowledged);
        Assert.Equal(2, revoked.State.Revision.Version); Assert.Equal(granted.State.Revision.Generation, revoked.State.Revision.Generation);
        Assert.Equal(revoked.State, (await f.Acknowledgements(p).ReadAsync(a)).State);
    }

    [LocalPostgresFact]
    public async Task PolicyChangesRequireNewExplicitRevisionAwareAcknowledgement()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var r = f.Acknowledgements(p); var a = f.Actor();
        var v1 = await r.AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null); p.Version = "synthetic-notice-v2";
        Assert.Equal("synthetic-notice-v1", (await r.ReadAsync(a)).State!.PolicyVersion);
        Assert.Equal(AiAcknowledgementOutcome.Conflict, (await r.AcknowledgeAsync(a, "synthetic-notice-v1", Guid.NewGuid(), v1.State!.Revision)).Outcome);
        var v2 = await r.AcknowledgeAsync(a, p.Version, Guid.NewGuid(), v1.State.Revision);
        Assert.Equal(AiAcknowledgementOutcome.Saved, v2.Outcome); Assert.Equal(p.Version, v2.State!.PolicyVersion); Assert.Equal(2, v2.State.Revision.Version);
    }

    [LocalPostgresFact]
    public async Task GrantRevokeGrantInvalidatesOldRevisionDespiteEqualContent()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var r = f.Acknowledgements(p); var a = f.Actor();
        var first = await r.AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null);
        var revoked = await r.RevokeAsync(a, Guid.NewGuid(), first.State!.Revision);
        var latest = await r.AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), revoked.State!.Revision);
        Assert.True(latest.State!.Acknowledged); Assert.Equal(3, latest.State.Revision.Version);
        Assert.Equal(AiAcknowledgementOutcome.Conflict, (await r.RevokeAsync(a, Guid.NewGuid(), first.State.Revision)).Outcome);
        Assert.Equal(latest.State, (await r.ReadAsync(a)).State);
    }

    [LocalPostgresFact]
    public async Task ConcurrentSameProfileGrantsUseIndependentConnectionsAndOneExpectedRevisionWins()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var a = f.Actor();
        var results = await Task.WhenAll(f.Acknowledgements(p).AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null),
            f.Acknowledgements(p).AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null));
        Assert.Single(results, x => x.Outcome == AiAcknowledgementOutcome.Saved);
        Assert.Single(results, x => x.Outcome == AiAcknowledgementOutcome.Conflict);
        Assert.Equal(1, (await f.Acknowledgements(p).ReadAsync(a)).State!.Revision.Version);
    }

    [LocalPostgresFact]
    public async Task ExactOperationReplayDoesNotAdvanceRevisionOrSwitchMeaning()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var r = f.Acknowledgements(p); var a = f.Actor(); var op = Guid.NewGuid();
        var first = await r.AcknowledgeAsync(a, p.Version!, op, null);
        Assert.Equal(first, await r.AcknowledgeAsync(a, p.Version!, op, null));
        Assert.Equal(AiAcknowledgementOutcome.Conflict, (await r.RevokeAsync(a, op, first.State!.Revision)).Outcome);
    }

    [LocalPostgresFact]
    public async Task MissingStateRevocationFencesStaleInitialGrant()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var r = f.Acknowledgements(p); var a = f.Actor();
        var revoked = await r.RevokeAsync(a, Guid.NewGuid(), null);
        Assert.Equal(AiAcknowledgementOutcome.Saved, revoked.Outcome); Assert.False(revoked.State!.Acknowledged);
        Assert.Equal(AiAcknowledgementOutcome.Conflict, (await r.AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null)).Outcome);
    }

    [LocalPostgresFact]
    public async Task AccountProfileAndModuleClaimsNeverEstablishAuthorization()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var r = f.Acknowledgements(p); var a = f.Actor();
        foreach(var foreign in new[] { new AiAuthorizedActor(Guid.NewGuid(), a.ModuleProfileId, AiModule.Learning),
            new AiAuthorizedActor(a.UserId, Guid.NewGuid(), AiModule.Learning), new AiAuthorizedActor(a.UserId, a.ModuleProfileId, AiModule.Hobbies) })
        {
            Assert.Equal(AiAcknowledgementOutcome.Denied, (await r.ReadAsync(foreign)).Outcome);
            Assert.Equal(AiAcknowledgementOutcome.Denied, (await r.AcknowledgeAsync(foreign, p.Version!, Guid.NewGuid(), null)).Outcome);
        }
        Assert.Null((await r.ReadAsync(a)).State);
    }

    [LocalPostgresFact]
    public async Task ProfileRevocationBeforeCommitRollsBackPersistedEvidence()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var a = f.Actor();
        var r = f.Acknowledgements(p, checkpoint: (phase, _) => { if(phase == "prepared") f.Owner.Denied = true; return Task.CompletedTask; });
        Assert.Equal(AiAcknowledgementOutcome.Denied, (await r.AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null)).Outcome);
        f.Owner.Denied = false; Assert.Null((await f.Acknowledgements(p).ReadAsync(a)).State);
    }

    [LocalPostgresFact]
    public async Task PolicyChangeDuringPreparationRollsBackWithoutClaimingDispatchFence()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var a = f.Actor();
        var r = f.Acknowledgements(p, checkpoint: (phase, _) => { if(phase == "prepared") p.Version = "synthetic-notice-v2"; return Task.CompletedTask; });
        Assert.Equal(AiAcknowledgementOutcome.Unavailable, (await r.AcknowledgeAsync(a, "synthetic-notice-v1", Guid.NewGuid(), null)).Outcome);
        Assert.Null((await f.Acknowledgements(p).ReadAsync(a)).State);
    }

    [LocalPostgresFact]
    public async Task CancellationAfterPreparationRollsBackAndPropagates()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var a = f.Actor(); using var cts = new CancellationTokenSource();
        var r = f.Acknowledgements(p, checkpoint: (phase, token) => { if(phase == "prepared") cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null, cts.Token));
        Assert.Null((await f.Acknowledgements(p).ReadAsync(a)).State);
    }

    [LocalPostgresFact]
    public async Task NoticeEvidenceNeverAutomaticallySatisfiesIndependentProcessingPrivacyGate()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var a = f.Actor();
        Assert.Equal(AiAcknowledgementOutcome.Saved, (await f.Acknowledgements(p).AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null)).Outcome);
        Assert.NotEqual(AiPrivacyNoticeGateOutcome.Satisfied, await new DenyAllAiPrivacyNoticeGate().EvaluateAsync(a));
    }

    [LocalPostgresFact]
    public async Task RuntimeCannotDeleteEvidenceOrRebindOwnershipAndForceRlsRemainsEnabled()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var a = f.Actor();
        await f.Acknowledgements(p).AcknowledgeAsync(a, p.Version!, Guid.NewGuid(), null);
        await using var owner = await f.BoundOwner();
        var flags = await owner.Database.SqlQueryRaw<string>("SELECT relrowsecurity::text||'|'||relforcerowsecurity::text AS \"Value\" FROM pg_class WHERE oid='andril_ai_isolated.privacy_acknowledgements'::regclass").SingleAsync();
        Assert.Equal("true|true", flags);
        await using var runtime = f.OpenRuntime();
        var privileges = await runtime.Database.SqlQueryRaw<string>("SELECT has_table_privilege(current_user,'andril_ai_isolated.privacy_acknowledgements','DELETE')::text||'|'||has_column_privilege(current_user,'andril_ai_isolated.privacy_acknowledgements','account_id','UPDATE')::text||'|'||has_column_privilege(current_user,'andril_ai_isolated.privacy_acknowledgements','generation','UPDATE')::text AS \"Value\"").SingleAsync();
        Assert.Equal("false|false|false", privileges);
        Assert.Empty(await runtime.Set<IsolatedAiAcknowledgementRow>().AsNoTracking().ToListAsync());
    }
}
