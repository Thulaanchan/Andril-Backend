using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.AI;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SocialContextualConsentRepositoryPostgresTests
{
    [LocalPostgresFact]
    public async Task ConsentIsConsumedOnceAndWithdrawalFencesTheExactPendingAttempt()
    {
        await using var fixture = await ConsentFixture.CreateAsync();
        var reference = await fixture.AddOperationAsync();
        var offer = await fixture.ApplyAsync(new AiConsentOfferRequest(reference, Guid.NewGuid()));
        Assert.Equal(AiConsentOutcome.Offered, offer.Outcome);
        var decision = await fixture.RecordDecisionAsync(reference, offer.Snapshot!.Revision, AiConsentDecision.Allow);
        var grant = await fixture.ApplyAsync(decision);
        Assert.Equal(AiConsentOutcome.DecisionRecorded, grant.Outcome);
        Assert.Equal(grant.Snapshot!.GrantedAtUtc, (await fixture.ApplyAsync(decision)).Snapshot!.GrantedAtUtc);
        var attempt = await fixture.IssueAttemptAsync(reference);
        var start = new AiConsentStartRequest(reference, Guid.NewGuid(), grant.Snapshot.Revision, attempt);
        var results = await Task.WhenAll(fixture.ApplyAsync(start), fixture.ApplyAsync(start));
        var started = Assert.Single(results, result => result.Outcome == AiConsentOutcome.Started);
        Assert.Single(results, result => result.Outcome == AiConsentOutcome.AlreadyRecorded);
        var pending = new AiConsentPendingReference(reference, started.Snapshot!.Revision, attempt);
        Assert.Equal(AiConsentOutcome.ContinuationVerified, (await fixture.Repository.CheckPendingAsync(pending, fixture.Hint(reference))).Outcome);

        Assert.Equal(AiConsentOutcome.Withdrawn, (await fixture.ApplyAsync(new AiConsentWithdrawalRequest(reference, Guid.NewGuid()))).Outcome);
        Assert.Equal(AiConsentOutcome.Revoked, (await fixture.Repository.CheckPendingAsync(pending, fixture.Hint(reference))).Outcome);
        await using var db = fixture.Open();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::integer AS "Value" FROM andril_consent_fixture.admissions WHERE fenced""").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::integer AS "Value" FROM andril_social_ai.consent_decision_audit""").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task SuppressionExpiryAndEpochRotationFailClosedWithoutRevivingConsent()
    {
        await using var fixture = await ConsentFixture.CreateAsync();
        var first = await fixture.AddOperationAsync();
        var offer = await fixture.ApplyAsync(new AiConsentOfferRequest(first, Guid.NewGuid()));
        var declined = await fixture.RecordDecisionAsync(first, offer.Snapshot!.Revision, AiConsentDecision.NotNow);
        Assert.Equal(AiConsentOutcome.DecisionRecorded, (await fixture.ApplyAsync(declined)).Outcome);
        var second = await fixture.AddOperationAsync(first.Operation.ConversationId);
        Assert.Equal(AiConsentOutcome.Suppressed, (await fixture.ApplyAsync(new AiConsentOfferRequest(second, Guid.NewGuid()))).Outcome);
        var evidence = await fixture.RecordEventAsync(second, "new", null, null);
        var confirmedRequest = await fixture.ApplyAsync(new AiConsentOfferRequest(second, Guid.NewGuid(), evidence));
        Assert.Equal(AiConsentState.AwaitingConsent, confirmedRequest.Snapshot!.State);

        var decision = await fixture.RecordDecisionAsync(second, confirmedRequest.Snapshot.Revision, AiConsentDecision.Allow);
        var grant = await fixture.ApplyAsync(decision);
        Assert.Equal(AiConsentOutcome.DecisionRecorded, grant.Outcome);
        var attempt = await fixture.IssueAttemptAsync(second);
        await using var db = fixture.Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_social_ai.consents SET offered_at_utc = clock_timestamp() - interval '6 minutes',
                granted_at_utc = clock_timestamp() - interval '5 minutes' WHERE operation_id = {second.Operation.OperationId}
            """);
        Assert.Equal(AiConsentOutcome.Expired, (await fixture.ApplyAsync(
            new AiConsentStartRequest(second, Guid.NewGuid(), grant.Snapshot!.Revision, attempt))).Outcome);

        var pending = await fixture.AddOperationAsync();
        Assert.Equal(AiConsentOutcome.Offered, (await fixture.ApplyAsync(new AiConsentOfferRequest(pending, Guid.NewGuid()))).Outcome);
        var otherConversation = pending with { Operation = pending.Operation with { ConversationId = Guid.NewGuid() } };
        Assert.Equal(AiConsentOutcome.Denied, (await fixture.Repository.ReadStatusAsync(otherConversation, fixture.Hint(otherConversation))).Outcome);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_consent_fixture.scopes SET session_epoch = gen_random_uuid(), module_epoch = gen_random_uuid() WHERE operation_id = {pending.Operation.OperationId}");
        Assert.Equal(AiConsentOutcome.Denied, (await fixture.Repository.ReadStatusAsync(pending, fixture.Hint(pending))).Outcome);
        var eventId = await fixture.RecordEventAsync(pending, "system", null, (int)AiConsentRevocationReason.ModuleSwitch);
        var invalidation = new AiConsentSystemInvalidation(eventId, pending, AiConsentRevocationReason.ModuleSwitch);
        Assert.Equal(AiConsentSystemOutcome.Invalidated, (await fixture.Repository.InvalidateFromSystemAsync(invalidation)).Outcome);
        Assert.Equal(AiConsentSystemOutcome.AlreadyInvalidated, (await fixture.Repository.InvalidateFromSystemAsync(invalidation)).Outcome);
        Assert.Equal(AiConsentSystemOutcome.Denied, (await fixture.Repository.InvalidateFromSystemAsync(invalidation with { EventId = Guid.NewGuid() })).Outcome);
    }

    [LocalPostgresFact]
    public async Task CancellingAWaitForTheConsentStoreLeavesNoOfferOrJournalEntry()
    {
        await using var fixture = await ConsentFixture.CreateAsync();
        var reference = await fixture.AddOperationAsync();
        await using var blocker = fixture.Open();
        await using var fence = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("LOCK TABLE andril_social_ai.consents IN ACCESS EXCLUSIVE MODE");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var offer = fixture.Repository.ApplyAsync(new AiConsentOfferRequest(reference, Guid.NewGuid()), fixture.Hint(reference), deadline.Token);
        await using var observer = fixture.Open();
        while (!await observer.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (SELECT FROM pg_locks WHERE relation = 'andril_social_ai.consents'::regclass AND NOT granted) AS "Value"
            """).SingleAsync(deadline.Token))
        {
            if (offer.IsCompleted) throw new InvalidOperationException("Consent request did not wait on PostgreSQL.");
            await Task.Delay(20, deadline.Token);
        }
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => offer);
        await fence.RollbackAsync();
        Assert.Equal(0, await observer.Database.SqlQueryRaw<int>("""SELECT count(*)::integer AS "Value" FROM andril_social_ai.consents""").SingleAsync());
        Assert.Equal(0, await observer.Database.SqlQueryRaw<int>("""SELECT count(*)::integer AS "Value" FROM andril_social_ai.consent_requests""").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task CurrentSocialLifecycleRotatesTheModuleEpochAndRevokesPendingConsent()
    {
        await using var fixture = await ConsentFixture.CreateAsync();
        var old = await fixture.AddOperationAsync();
        Assert.Equal(AiConsentOutcome.Offered, (await fixture.ApplyAsync(new AiConsentOfferRequest(old, Guid.NewGuid()))).Outcome);

        await using var db = fixture.Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.account_module_lifecycles SET state = 'inactive', revision = revision + 1, state_changed_at_utc = clock_timestamp()
            WHERE account_id = {old.Operation.Actor.UserId} AND module = 'social'
            """);
        Assert.Null(await fixture.Repository.ReadLifecycleAsync(old.Operation));
        Assert.Equal(6, await db.Database.SqlQuery<int>($"""
            SELECT state AS "Value" FROM andril_social_ai.consents WHERE operation_id = {old.Operation.OperationId}
            """).SingleAsync());
        Assert.Equal(1, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::integer AS "Value" FROM andril_social_ai.consent_invalidations WHERE operation_id = {old.Operation.OperationId}
            """).SingleAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.account_module_lifecycles SET state = 'active', revision = revision + 1, state_changed_at_utc = clock_timestamp()
            WHERE account_id = {old.Operation.Actor.UserId} AND module = 'social'
            """);
        var current = await fixture.AddOperationAsync(old.Operation.ConversationId);
        Assert.NotEqual(old.ModuleEpoch, current.ModuleEpoch);
        Assert.Equal(AiConsentOutcome.Denied, (await fixture.Repository.ReadStatusAsync(old, fixture.Hint(old))).Outcome);
        Assert.Equal(AiConsentOutcome.Offered, (await fixture.ApplyAsync(new AiConsentOfferRequest(current, Guid.NewGuid()))).Outcome);
    }

    private sealed class ConsentFixture(SocialLifecycleDatabase database, AiAuthorizedActor actor) : IAsyncDisposable
    {
        private readonly DbContextOptions<SocialDbContext> options = new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(database.Database.ConnectionString).Options;
        public IAiContextualConsentRepository Repository => new PostgresSocialContextualConsentRepository(options, new FixtureAuthority());
        public SocialDbContext Open() => new(options);
        public ValueTask DisposeAsync() => database.DisposeAsync();

        public static async Task<ConsentFixture> CreateAsync()
        {
            var database = await SocialLifecycleDatabase.CreateAsync();
            try
            {
                var owner = await SocialQuestionnaireHttpPostgresTests.SeedAsync(database);
                var fixture = new ConsentFixture(database, new AiAuthorizedActor(owner.Account, owner.Profile, AiModule.Social));
                await using var db = fixture.Open();
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE SCHEMA andril_consent_fixture;
                    CREATE TABLE andril_consent_fixture.scopes (
                        operation_id uuid PRIMARY KEY, account_id uuid NOT NULL, profile_id uuid NOT NULL, conversation_id uuid NOT NULL,
                        session_epoch uuid NOT NULL, module_epoch uuid NOT NULL, attempt_id uuid NULL);
                    CREATE TABLE andril_consent_fixture.events (
                        id uuid PRIMARY KEY, operation_id uuid NOT NULL, kind text NOT NULL, revision uuid NULL, decision integer NULL);
                    CREATE TABLE andril_consent_fixture.admissions (
                        attempt_id uuid PRIMARY KEY, operation_id uuid NOT NULL UNIQUE, fenced boolean NOT NULL DEFAULT false);
                    """);
                return fixture;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async Task<AiConsentReference> AddOperationAsync(Guid? conversation = null)
        {
            var operation = new AiOperationReference(actor, conversation ?? Guid.NewGuid(), Guid.NewGuid());
            await using var db = Open();
            var epoch = await db.Database.SqlQuery<Guid>($"""
                SELECT epoch AS "Value" FROM andril_social_ai.module_epochs WHERE account_id = {actor.UserId}
                """).SingleAsync();
            var reference = new AiConsentReference(operation, new("fixture-purpose", "v1", "self"), Guid.NewGuid(), epoch);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_consent_fixture.scopes(operation_id, account_id, profile_id, conversation_id, session_epoch, module_epoch)
                VALUES ({operation.OperationId}, {actor.UserId}, {actor.ModuleProfileId}, {operation.ConversationId},
                    {reference.SessionEpoch}, {reference.ModuleEpoch})
                """);
            return reference;
        }

        public AiConsentLifecycle Hint(AiConsentReference reference) => new(reference.Operation, reference.SessionEpoch,
            reference.ModuleEpoch, DateTimeOffset.UtcNow, true, true, true, true);

        public Task<AiConsentResult> ApplyAsync(AiConsentRequest request) => Repository.ApplyAsync(request, Hint(request.Reference));

        public async Task<AiConsentDecisionRequest> RecordDecisionAsync(AiConsentReference reference, Guid offerRevision, AiConsentDecision choice)
        {
            var eventId = await RecordEventAsync(reference, "decision", offerRevision, (int)choice);
            return new(reference, Guid.NewGuid(), offerRevision, choice, eventId);
        }

        public async Task<Guid> RecordEventAsync(AiConsentReference reference, string kind, Guid? revision, int? decision)
        {
            var id = Guid.NewGuid();
            await using var db = Open();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_consent_fixture.events VALUES ({id}, {reference.Operation.OperationId}, {kind}, {revision}, {decision})
                """);
            return id;
        }

        public async Task<Guid> IssueAttemptAsync(AiConsentReference reference)
        {
            var id = Guid.NewGuid();
            await using var db = Open();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE andril_consent_fixture.scopes SET attempt_id = {id} WHERE operation_id = {reference.Operation.OperationId}
                """);
            return id;
        }
    }

    private sealed class FixtureAuthority : ISocialConsentTransactionAuthority
    {
        public async Task<SocialConsentAuthorityEvidence?> ResolveAsync(SocialDbContext db, AiOperationReference operation,
            AiConsentPurpose? purpose, CancellationToken token)
        {
            if (purpose is not null && purpose != new AiConsentPurpose("fixture-purpose", "v1", "self")) return null;
            var row = await db.Database.SqlQuery<Epochs>($"""
                SELECT session_epoch AS "Session", module_epoch AS "Module" FROM andril_consent_fixture.scopes
                WHERE operation_id = {operation.OperationId} AND account_id = {operation.Actor.UserId}
                    AND profile_id = {operation.Actor.ModuleProfileId} AND conversation_id = {operation.ConversationId}
                FOR UPDATE
                """).SingleOrDefaultAsync(token);
            return row is null ? null : new(new(operation, row.Session, row.Module, default, true, true, true, true),
                new AiConsentNotice("fixture-notice", "Fixture recommendation", "Fixture self-only conversation"));
        }

        public Task<bool> VerifyNewRequestAsync(SocialDbContext db, AiConsentOfferRequest request, CancellationToken token) =>
            HasEvent(db, request.Reference, request.ExplicitNewUserRequestEvidenceId, "new", null, null, token);

        public Task<bool> VerifyDecisionAsync(SocialDbContext db, AiConsentDecisionRequest request, CancellationToken token) =>
            HasEvent(db, request.Reference, request.DecisionEvidenceId, "decision", request.OfferRevision, (int)request.Decision, token);

        public async Task<bool> TryAdmitAttemptAsync(SocialDbContext db, AiConsentStartRequest request, CancellationToken token) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_consent_fixture.admissions(attempt_id, operation_id)
                SELECT attempt_id, operation_id FROM andril_consent_fixture.scopes
                WHERE operation_id = {request.Reference.Operation.OperationId} AND attempt_id = {request.AttemptId}
                ON CONFLICT DO NOTHING
                """, token) == 1;

        public async Task<bool> FencePendingAsync(SocialDbContext db, AiConsentReference reference, Guid attempt, CancellationToken token) =>
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE andril_consent_fixture.admissions SET fenced = true
                WHERE operation_id = {reference.Operation.OperationId} AND attempt_id = {attempt}
                """, token) == 1;

        public Task<bool> VerifySystemEventAsync(SocialDbContext db, AiConsentSystemInvalidation evidence, CancellationToken token) =>
            HasEvent(db, evidence.Reference, evidence.EventId, "system", null, (int)evidence.Reason, token);

        private static Task<bool> HasEvent(SocialDbContext db, AiConsentReference reference, Guid? id, string kind, Guid? revision,
            int? decision, CancellationToken token) => db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT FROM andril_consent_fixture.events WHERE id = {id} AND operation_id = {reference.Operation.OperationId}
                    AND kind = {kind} AND revision IS NOT DISTINCT FROM {revision} AND decision IS NOT DISTINCT FROM {decision}) AS "Value"
                """).SingleAsync(token);

        private sealed record Epochs(Guid Session, Guid Module);
    }
}
