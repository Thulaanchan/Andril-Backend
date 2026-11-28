using System.Security.Cryptography;
using System.Text;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.AI;

/// <summary>Durable Social consent and revocation. Dispatch and the live authority remain separate dependencies.</summary>
public sealed class PostgresSocialContextualConsentRepository(DbContextOptions<SocialDbContext> options,
    ISocialConsentTransactionAuthority authority) : IAiContextualConsentRepository
{
    public Task<AiConsentLifecycle?> ReadLifecycleAsync(AiOperationReference operation, CancellationToken cancellationToken = default) =>
        RunAsync<AiConsentLifecycle?>(operation, null, async (db, ct) =>
        {
            var evidence = await authority.ResolveAsync(db, operation, null, ct);
            if (evidence?.Lifecycle.Operation != operation ||
                !await CurrentModuleEpochAsync(db, operation.Actor, evidence.Lifecycle.ModuleEpoch, ct)) return null;
            return evidence.Lifecycle with { NowUtc = await UtcAsync(db, ct) };
        }, cancellationToken);

    public Task<AiConsentResult> ApplyAsync(AiConsentRequest request, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AiContextualConsentPolicy.ValidReference(request.Reference) || request.RequestId == Guid.Empty)
            return Denied(cancellationToken);
        return RunAsync(request.Reference.Operation, new AiConsentResult(AiConsentOutcome.Denied), (db, ct) =>
            ApplyCoreAsync(db, request, ct), cancellationToken);
    }

    public Task<AiConsentResult> ReadStatusAsync(AiConsentReference reference, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
        ReadAsync(reference, null, cancellationToken);

    public Task<AiConsentResult> CheckPendingAsync(AiConsentPendingReference pending, AiConsentLifecycle lifecycle,
        CancellationToken cancellationToken = default) => ReadAsync(pending.Reference, pending, cancellationToken);

    public Task<AiConsentSystemResult> InvalidateFromSystemAsync(AiConsentSystemInvalidation evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var denied = new AiConsentSystemResult(evidence, AiConsentSystemOutcome.Denied);
        if (!AiContextualConsentPolicy.ValidReference(evidence.Reference) || evidence.EventId == Guid.Empty
            || evidence.Reason == AiConsentRevocationReason.Unknown || !Enum.IsDefined(evidence.Reason))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(denied);
        }
        return RunAsync(evidence.Reference.Operation, denied, async (db, ct) =>
        {
            if (!await authority.VerifySystemEventAsync(db, evidence, ct)) return denied;
            var current = await ReadStoredAsync(db, evidence.Reference, ct);
            if (current is null) return denied;
            if (current.State is AiConsentState.Completed)
                return new(evidence, AiConsentSystemOutcome.CompletedWithdrawalPolicyPending);
            if (current.State == AiConsentState.Revoked) return new(evidence, AiConsentSystemOutcome.AlreadyInvalidated);

            await db.Database.CurrentTransaction!.CreateSavepointAsync("system_revocation", ct);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_social_ai.consent_invalidations(event_id, operation_id, reason)
                VALUES ({evidence.EventId}, {evidence.Reference.Operation.OperationId}, {(int)evidence.Reason})
                ON CONFLICT DO NOTHING
                """, ct);
            if (inserted == 0 || !await FenceAsync(db, evidence.Reference, current, ct))
            {
                await db.Database.CurrentTransaction.RollbackToSavepointAsync("system_revocation", ct);
                return denied;
            }
            await UpdateAsync(db, evidence.Reference, current with { State = AiConsentState.Revoked, Revision = Guid.NewGuid() }, ct);
            return new(evidence, AiConsentSystemOutcome.Invalidated);
        }, cancellationToken);
    }

    private async Task<AiConsentResult> ApplyCoreAsync(SocialDbContext db, AiConsentRequest request, CancellationToken ct)
    {
        var reference = request.Reference;
        var evidence = await ResolveAsync(db, reference, ct);
        if (evidence is null) return new(AiConsentOutcome.Denied);
        var stored = await ReadStoredAsync(db, reference, ct);
        if (stored is not null && stored.NoticeRevision != evidence.ApprovedNotice!.Revision) return new(AiConsentOutcome.Denied);
        if (stored?.State == AiConsentState.Revoked) return new(AiConsentOutcome.Revoked);
        if (stored is not null && await ExpireAsync(db, reference, stored, evidence.Lifecycle.NowUtc, ct))
            return new(AiConsentOutcome.Expired);

        var digest = Digest(request);
        if (digest is null) return new(AiConsentOutcome.Denied);
        var previous = await db.Database.SqlQuery<byte[]>($"""
            SELECT request_digest AS "Value" FROM andril_social_ai.consent_requests
            WHERE operation_id = {reference.Operation.OperationId} AND request_id = {request.RequestId}
            """).SingleOrDefaultAsync(ct);
        if (previous is not null)
            return stored is not null && CryptographicOperations.FixedTimeEquals(previous, digest)
                ? Result(AiConsentOutcome.AlreadyRecorded, reference, stored, evidence, request.RequestId)
                : new(AiConsentOutcome.Conflict);

        if (stored is null)
        {
            if (request is not AiConsentOfferRequest offer) return new(AiConsentOutcome.Denied);
            var declined = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT FROM andril_social_ai.consents WHERE account_id = {reference.Operation.Actor.UserId}
                    AND profile_id = {reference.Operation.Actor.ModuleProfileId} AND conversation_id = {reference.Operation.ConversationId}
                    AND state = {(int)AiConsentState.Declined}) AS "Value"
                """).SingleAsync(ct);
            if ((declined || offer.ExplicitNewUserRequestEvidenceId.HasValue) &&
                (offer.ExplicitNewUserRequestEvidenceId is not Guid interaction || interaction == Guid.Empty ||
                 !await authority.VerifyNewRequestAsync(db, offer, ct))) return new(AiConsentOutcome.Suppressed);

            stored = new StoredConsent(evidence.ApprovedNotice!.Revision, AiConsentState.AwaitingConsent, Guid.NewGuid(),
                evidence.Lifecycle.NowUtc, null, null, null);
            var actor = reference.Operation.Actor;
            await db.Database.CurrentTransaction!.CreateSavepointAsync("offer", ct);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_social_ai.consents(operation_id, account_id, profile_id, conversation_id, session_epoch, module_epoch,
                    purpose_id, purpose_version, scope_id, notice_revision, state, revision, offered_at_utc)
                VALUES ({reference.Operation.OperationId}, {actor.UserId}, {actor.ModuleProfileId}, {reference.Operation.ConversationId},
                    {reference.SessionEpoch}, {reference.ModuleEpoch}, {reference.Purpose.Id}, {reference.Purpose.Version}, {reference.Purpose.ScopeId},
                    {stored.NoticeRevision}, {(int)stored.State}, {stored.Revision}, {stored.OfferedAtUtc})
                ON CONFLICT DO NOTHING
                """, ct);
            if (inserted == 0) return new(AiConsentOutcome.Conflict);
            if (!await JournalAsync(db, request, digest, offer.ExplicitNewUserRequestEvidenceId, ct))
            {
                await db.Database.CurrentTransaction.RollbackToSavepointAsync("offer", ct);
                return new(AiConsentOutcome.Conflict);
            }
            return Result(AiConsentOutcome.Offered, reference, stored, evidence, request.RequestId);
        }

        await db.Database.CurrentTransaction!.CreateSavepointAsync("consent_change", ct);
        var (state, outcome) = await TransitionAsync(db, request, stored, evidence, ct);
        if (state is null)
        {
            await db.Database.CurrentTransaction.RollbackToSavepointAsync("consent_change", ct);
            if (outcome == AiConsentOutcome.Expired)
                await ExpireAsync(db, reference, stored, await UtcAsync(db, ct), ct);
            return new(outcome);
        }
        if (!await JournalAsync(db, request, digest, (request as AiConsentDecisionRequest)?.DecisionEvidenceId, ct))
        {
            await db.Database.CurrentTransaction.RollbackToSavepointAsync("consent_change", ct);
            return new(AiConsentOutcome.Conflict);
        }
        await UpdateAsync(db, reference, state, ct);
        if (request is AiConsentDecisionRequest decision)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_social_ai.consent_decision_audit(event_id, operation_id, decision, purpose_version, occurred_at_utc)
                VALUES ({decision.DecisionEvidenceId}, {reference.Operation.OperationId}, {(int)decision.Decision}, {reference.Purpose.Version},
                    {state.GrantedAtUtc ?? await UtcAsync(db, ct)})
                """, ct);
        return Result(outcome, reference, state, evidence with { Lifecycle = evidence.Lifecycle with { NowUtc = await UtcAsync(db, ct) } }, request.RequestId);
    }

    private async Task<(StoredConsent? State, AiConsentOutcome Outcome)> TransitionAsync(SocialDbContext db,
        AiConsentRequest request, StoredConsent stored, SocialConsentAuthorityEvidence evidence, CancellationToken ct)
    {
        switch (request)
        {
            case AiConsentDecisionRequest decision when stored.State == AiConsentState.AwaitingConsent && stored.Revision == decision.OfferRevision:
                if (!AiContextualConsentPolicy.ValidDecision(decision.Decision) || decision.DecisionEvidenceId == Guid.Empty
                    || !await authority.VerifyDecisionAsync(db, decision, ct)) return (null, AiConsentOutcome.Denied);
                var grant = decision.Decision == AiConsentDecision.Allow;
                return (stored with { State = grant ? AiConsentState.GrantedUnused : AiConsentState.Declined, Revision = Guid.NewGuid(),
                    GrantedAtUtc = grant ? await UtcAsync(db, ct) : null }, AiConsentOutcome.DecisionRecorded);
            case AiConsentStartRequest start when stored.State == AiConsentState.GrantedUnused && stored.Revision == start.ConsentRevision:
                if (start.AttemptId == Guid.Empty ||
                    !AiContextualConsentPolicy.CanStart(stored.Snapshot(start.Reference, evidence.ApprovedNotice!), start.Reference, evidence.Lifecycle) ||
                    !await authority.TryAdmitAttemptAsync(db, start, ct)) return (null, AiConsentOutcome.Denied);
                var now = await UtcAsync(db, ct);
                if (now >= AiContextualConsentPolicy.UnusedExpiresAt(stored.GrantedAtUtc!.Value)) return (null, AiConsentOutcome.Expired);
                return (stored with { State = AiConsentState.Started, Revision = Guid.NewGuid(), StartedAtUtc = now, AttemptId = start.AttemptId },
                    AiConsentOutcome.Started);
            case AiConsentWithdrawalRequest when stored.State == AiConsentState.Completed:
                return (null, AiConsentOutcome.CompletedWithdrawalPolicyPending);
            case AiConsentWithdrawalRequest:
                if (!await FenceAsync(db, request.Reference, stored, ct)) return (null, AiConsentOutcome.Denied);
                return (stored with { State = AiConsentState.Revoked, Revision = Guid.NewGuid() }, AiConsentOutcome.Withdrawn);
            default:
                return (null, AiConsentOutcome.Conflict);
        }
    }

    private Task<AiConsentResult> ReadAsync(AiConsentReference reference, AiConsentPendingReference? pending, CancellationToken token) =>
        !AiContextualConsentPolicy.ValidReference(reference) ? Denied(token) :
        RunAsync(reference.Operation, new AiConsentResult(AiConsentOutcome.Denied), async (db, ct) =>
        {
            var evidence = await ResolveAsync(db, reference, ct);
            if (evidence is null) return new(AiConsentOutcome.Denied);
            var stored = await ReadStoredAsync(db, reference, ct);
            if (stored is null || stored.NoticeRevision != evidence.ApprovedNotice!.Revision) return new(AiConsentOutcome.Denied);
            if (stored.State == AiConsentState.Revoked) return new(AiConsentOutcome.Revoked);
            if (await ExpireAsync(db, reference, stored, evidence.Lifecycle.NowUtc, ct)) return new(AiConsentOutcome.Expired);
            if (pending is not null && !AiContextualConsentPolicy.CanContinue(stored.Snapshot(reference, evidence.ApprovedNotice), reference,
                evidence.Lifecycle, pending.AttemptId, pending.ConsentRevision)) return new(AiConsentOutcome.Denied);
            return Result(pending is null ? AiConsentOutcome.Status : AiConsentOutcome.ContinuationVerified, reference, stored, evidence, null);
        }, token);

    private async Task<SocialConsentAuthorityEvidence?> ResolveAsync(SocialDbContext db, AiConsentReference reference, CancellationToken ct)
    {
        var evidence = await authority.ResolveAsync(db, reference.Operation, reference.Purpose, ct);
        if (evidence?.ApprovedNotice is not { } notice || string.IsNullOrWhiteSpace(notice.Revision) ||
            string.IsNullOrWhiteSpace(notice.RecommendationDescription) || string.IsNullOrWhiteSpace(notice.CurrentConversationUseDescription))
            return null;
        var lifecycle = evidence.Lifecycle with { NowUtc = await UtcAsync(db, ct) };
        return AiContextualConsentPolicy.ValidLifecycle(reference, lifecycle) &&
            await CurrentModuleEpochAsync(db, reference.Operation.Actor, reference.ModuleEpoch, ct)
                ? evidence with { Lifecycle = lifecycle } : null;
    }

    private async Task<T> RunAsync<T>(AiOperationReference operation, T denied,
        Func<SocialDbContext, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!AiOperationPolicy.ValidReference(operation) || operation.Actor.Module != AiModule.Social) return denied;
        await using var db = new SocialDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT andril_social.social_safety_lock()", ct);
        var key = $"andril.social.consent/{operation.Actor.ModuleProfileId:D}/{operation.ConversationId:D}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", ct);
        var result = await action(db, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static Task<StoredConsent?> ReadStoredAsync(SocialDbContext db, AiConsentReference reference, CancellationToken ct) =>
        db.Database.SqlQuery<StoredConsent>($"""
            SELECT notice_revision AS "NoticeRevision", state AS "State", revision AS "Revision", offered_at_utc AS "OfferedAtUtc",
                granted_at_utc AS "GrantedAtUtc", started_at_utc AS "StartedAtUtc", attempt_id AS "AttemptId"
            FROM andril_social_ai.consents WHERE operation_id = {reference.Operation.OperationId}
                AND account_id = {reference.Operation.Actor.UserId} AND profile_id = {reference.Operation.Actor.ModuleProfileId}
                AND conversation_id = {reference.Operation.ConversationId} AND session_epoch = {reference.SessionEpoch}
                AND module_epoch = {reference.ModuleEpoch} AND purpose_id = {reference.Purpose.Id}
                AND purpose_version = {reference.Purpose.Version} AND scope_id = {reference.Purpose.ScopeId}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);

    private static Task UpdateAsync(SocialDbContext db, AiConsentReference reference, StoredConsent state, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_social_ai.consents SET state = {(int)state.State}, revision = {state.Revision}, granted_at_utc = {state.GrantedAtUtc},
                started_at_utc = {state.StartedAtUtc}, attempt_id = {state.AttemptId} WHERE operation_id = {reference.Operation.OperationId}
            """, ct);

    private static async Task<bool> ExpireAsync(SocialDbContext db, AiConsentReference reference, StoredConsent state,
        DateTimeOffset now, CancellationToken ct)
    {
        if (state.State == AiConsentState.Expired) return true;
        if (state.State != AiConsentState.GrantedUnused || now < AiContextualConsentPolicy.UnusedExpiresAt(state.GrantedAtUtc!.Value))
            return false;
        await UpdateAsync(db, reference, state with { State = AiConsentState.Expired, Revision = Guid.NewGuid() }, ct);
        return true;
    }

    private Task<bool> FenceAsync(SocialDbContext db, AiConsentReference reference, StoredConsent state, CancellationToken ct) =>
        state.State == AiConsentState.Started && state.AttemptId is Guid attempt
            ? authority.FencePendingAsync(db, reference, attempt, ct)
            : Task.FromResult(true);

    private static Task<bool> CurrentModuleEpochAsync(SocialDbContext db, AiAuthorizedActor actor, Guid epoch, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            SELECT andril_social_ai.is_current_module_epoch({actor.UserId}, {actor.ModuleProfileId}, {epoch}) AS "Value"
            """).SingleAsync(ct);

    private static Task<DateTimeOffset> UtcAsync(SocialDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private static async Task<bool> JournalAsync(SocialDbContext db, AiConsentRequest request, byte[] digest, Guid? interaction, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_social_ai.consent_requests(operation_id, request_id, request_digest, interaction_id)
            VALUES ({request.Reference.Operation.OperationId}, {request.RequestId}, {digest}, {interaction}) ON CONFLICT DO NOTHING
            """, ct) == 1;

    private static byte[]? Digest(AiConsentRequest request)
    {
        var action = request switch
        {
            AiConsentOfferRequest offer => $"offer:{offer.ExplicitNewUserRequestEvidenceId:D}",
            AiConsentDecisionRequest decision => $"decision:{decision.OfferRevision:D}:{(int)decision.Decision}:{decision.DecisionEvidenceId:D}",
            AiConsentStartRequest start => $"start:{start.ConsentRevision:D}:{start.AttemptId:D}",
            AiConsentWithdrawalRequest => "withdraw",
            _ => null
        };
        return action is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(action));
    }

    private static AiConsentResult Result(AiConsentOutcome outcome, AiConsentReference reference, StoredConsent state,
        SocialConsentAuthorityEvidence evidence, Guid? request) =>
        new(outcome, state.Snapshot(reference, evidence.ApprovedNotice!), evidence.Lifecycle, request);

    private static Task<AiConsentResult> Denied(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AiConsentResult(AiConsentOutcome.Denied));
    }

    private sealed record StoredConsent(string NoticeRevision, AiConsentState State, Guid Revision, DateTimeOffset OfferedAtUtc,
        DateTimeOffset? GrantedAtUtc, DateTimeOffset? StartedAtUtc, Guid? AttemptId)
    {
        public AiConsentSnapshot Snapshot(AiConsentReference reference, AiConsentNotice notice) =>
            new(reference, notice, State, Revision, OfferedAtUtc, GrantedAtUtc, StartedAtUtc, AttemptId);
    }
}
