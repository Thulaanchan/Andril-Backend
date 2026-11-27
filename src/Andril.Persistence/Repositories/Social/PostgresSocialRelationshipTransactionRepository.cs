using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.Social;

/// <summary>Serializes Social pair operations inside the trusted Accounts authorization transaction.</summary>
public sealed class PostgresSocialRelationshipTransactionRepository(
    AccountsDbContext accounts,
    DbContextOptions<SocialDbContext> socialOptions,
    ICurrentUser user,
    string runtimeRole) : ISocialRelationshipTransactionRepository
{
    private const string Savepoint = "andril_social_relationship";

    public Task<SocialRelationshipSnapshot> GetRelationshipStateAsync(SocialProfilePairKey pairKey, Guid requestingProfileId,
        CancellationToken cancellationToken = default) => ExecuteAsync(pairKey, requestingProfileId, async (db, target, token) =>
    {
        var match = await FindMatchAsync(db, pairKey, token);
        if (match is not null)
            return match.State == "active"
                ? new SocialRelationshipSnapshot(pairKey, SocialRelationshipState.Matched, ToMatch(match, pairKey), match.CreatedAtUtc)
                : new SocialRelationshipSnapshot(pairKey, SocialRelationshipState.Revoked, lastUpdatedUtc: match.RevokedAtUtc);

        var own = await FindInterestAsync(db, requestingProfileId, target, token);
        return own is null
            ? new SocialRelationshipSnapshot(pairKey, SocialRelationshipState.None)
            : new SocialRelationshipSnapshot(pairKey, OwnInterestState(own.IsPositive), lastUpdatedUtc: own.RecordedAtUtc);
    }, cancellationToken);

    public Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(SocialProfilePairKey pairKey, Guid actingProfileId,
        CancellationToken cancellationToken = default) => ExecuteAsync(pairKey, actingProfileId, async (db, target, token) =>
    {
        var row = await FindInterestAsync(db, actingProfileId, target, token);
        return row is null ? null : new SocialDirectionalInterest(pairKey, actingProfileId, row.IsPositive, row.RecordedAtUtc);
    }, cancellationToken);

    public Task<SocialDirectionalInterest?> GetReciprocalInterestAsync(SocialProfilePairKey pairKey, Guid actingProfileId,
        CancellationToken cancellationToken = default) => ExecuteAsync(pairKey, actingProfileId, async (db, target, token) =>
    {
        var row = await ReadReciprocalInterestAsync(db, actingProfileId, target, token);
        return row is null ? null : new SocialDirectionalInterest(pairKey, target, row.IsPositive, row.RecordedAtUtc);
    }, cancellationToken);

    public Task<SocialInterestTransactionResult> RecordInterestAndTryMatchAsync(SocialProfilePairKey pairKey,
        Guid actingProfileId, bool isPositive, DateTime timestampUtc, CancellationToken cancellationToken = default)
    {
        _ = new SocialDirectionalInterest(pairKey, actingProfileId, isPositive, timestampUtc);
        return ExecuteAsync(pairKey, actingProfileId, async (db, target, token) =>
        {
            var existing = await FindMatchAsync(db, pairKey, token);
            if (existing?.State == "active" && isPositive)
                return new SocialInterestTransactionResult(true, false, ToMatch(existing, pairKey), SocialRelationshipState.Matched);
            if (existing?.State == "revoked")
                return new SocialInterestTransactionResult(false, false, null, SocialRelationshipState.Revoked);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_social.social_directional_interests (actor_profile_id, target_profile_id, module, is_positive, recorded_at_utc)
                VALUES ({actingProfileId}, {target}, 'social', {isPositive}, {timestampUtc})
                ON CONFLICT (actor_profile_id, target_profile_id) DO UPDATE
                    SET is_positive = EXCLUDED.is_positive, recorded_at_utc = EXCLUDED.recorded_at_utc
                    WHERE social_directional_interests.is_positive <> EXCLUDED.is_positive
                """, token);

            if (!isPositive)
            {
                if (existing is not null) await RevokeAsync(db, existing, timestampUtc, token);
                return SocialInterestTransactionResult.InterestRecorded(SocialRelationshipState.Passed);
            }

            var reciprocal = await ReadReciprocalInterestAsync(db, actingProfileId, target, token);
            if (reciprocal?.IsPositive != true)
                return SocialInterestTransactionResult.InterestRecorded(SocialRelationshipState.PendingUnilateral);

            var matchTime = reciprocal.RecordedAtUtc > timestampUtc ? reciprocal.RecordedAtUtc : timestampUtc;
            var matchId = Guid.NewGuid();
            var created = await db.Database.SqlQuery<Guid?>($"""
                SELECT andril_social.try_form_social_match({actingProfileId}, {target}, {matchId}, {matchTime}) AS "Value"
                """).SingleAsync(token);
            if (created != matchId) throw new InvalidOperationException("The Social match transition could not be verified.");
            return SocialInterestTransactionResult.MatchEstablished(new SocialMatchEntity(matchId, pairKey, matchTime));
        }, cancellationToken);
    }

    public Task<SocialRevocationTransactionResult> RevokeMatchAsync(SocialProfilePairKey pairKey, Guid actingProfileId,
        DateTime timestampUtc, CancellationToken cancellationToken = default)
    {
        if (timestampUtc.Kind != DateTimeKind.Utc || timestampUtc == DateTime.MinValue || timestampUtc == DateTime.MaxValue)
            throw new ArgumentException("A finite UTC timestamp is required.", nameof(timestampUtc));

        return ExecuteAsync(pairKey, actingProfileId, async (db, _, token) =>
        {
            var match = await FindMatchAsync(db, pairKey, token);
            if (match?.State != "active") return new SocialRevocationTransactionResult(true, false, null, timestampUtc);
            var revokedAt = await RevokeAsync(db, match, timestampUtc, token);
            return new SocialRevocationTransactionResult(true, true, match.Id, revokedAt);
        }, cancellationToken);
    }

    private static async Task<DateTime> RevokeAsync(SocialDbContext db, SocialMatchRow match, DateTime requestedAt, CancellationToken token)
    {
        var revokedAt = requestedAt < match.CreatedAtUtc ? match.CreatedAtUtc : requestedAt;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_social.social_matches SET state = 'revoked', revoked_at_utc = {revokedAt}
            WHERE id = {match.Id} AND state = 'active'
            """, token);
        if (changed != 1) throw new InvalidOperationException("The Social match revocation could not be verified.");
        return revokedAt;
    }

    private static Task<SocialMatchRow?> FindMatchAsync(SocialDbContext db, SocialProfilePairKey pair, CancellationToken token) =>
        db.Set<SocialMatchRow>().AsNoTracking()
            .Where(row => row.FirstProfileId == pair.FirstProfileId && row.SecondProfileId == pair.SecondProfileId)
            .OrderByDescending(row => row.State == "active").ThenByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Id)
            .FirstOrDefaultAsync(token);

    private static Task<SocialDirectionalInterestRow?> FindInterestAsync(SocialDbContext db, Guid actor, Guid target, CancellationToken token) =>
        db.Set<SocialDirectionalInterestRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.ActorProfileId == actor && row.TargetProfileId == target, token);

    private static Task<ReciprocalInterest?> ReadReciprocalInterestAsync(SocialDbContext db, Guid actor, Guid target, CancellationToken token) =>
        db.Database.SqlQuery<ReciprocalInterest>($"""
            SELECT is_positive AS "IsPositive", recorded_at_utc AS "RecordedAtUtc"
            FROM andril_social.social_reciprocal_interest({actor}, {target})
            """).SingleOrDefaultAsync(token);

    private static SocialMatchEntity ToMatch(SocialMatchRow row, SocialProfilePairKey pair) => new(row.Id, pair, row.CreatedAtUtc);

    private static SocialRelationshipState OwnInterestState(bool isPositive) =>
        isPositive ? SocialRelationshipState.PendingUnilateral : SocialRelationshipState.Passed;

    private async Task<T> ExecuteAsync<T>(SocialProfilePairKey pair, Guid actor,
        Func<SocialDbContext, Guid, CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pair is null || !pair.Contains(actor) || !user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new ProfileAccessDeniedException();
        if (string.IsNullOrWhiteSpace(runtimeRole)) throw new InvalidOperationException("The Social relationship database role is required.");
        if (accounts.Database.CurrentTransaction is not { SupportsSavepoints: true } transaction)
            throw new InvalidOperationException("The Social relationship requires a current Accounts authorization transaction.");

        var accountId = await accounts.Accounts.AsNoTracking().Where(row => row.SupabaseUserId == subject)
            .Select(row => row.Id).SingleOrDefaultAsync(token);
        if (accountId == Guid.Empty) throw new ProfileAccessDeniedException();

        var options = new DbContextOptionsBuilder<SocialDbContext>(socialOptions).UseNpgsql(accounts.Database.GetDbConnection()).Options;
        await using var db = new SocialDbContext(options);
        await db.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
        await transaction.CreateSavepointAsync(Savepoint, token);
        try
        {
            var previous = await db.Database.SqlQueryRaw<SessionScope>("""
                SELECT current_setting('role') AS "Role", coalesce(current_setting('andril.subject', true), '') AS "Subject",
                    coalesce(current_setting('andril.account', true), '') AS "Account", coalesce(current_setting('andril.module', true), '') AS "Module"
                """).SingleAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('andril.subject', {subject.ToString()}, true), set_config('andril.account', {accountId.ToString()}, true),
                    set_config('andril.module', 'social', true), set_config('role', {runtimeRole}, true)
                """, token);

            var restricted = await db.Database.SqlQueryRaw<bool>("""
                SELECT (current_user <> session_user AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                    AND row_security_active('andril_social.social_directional_interests'::regclass)
                    AND row_security_active('andril_social.social_matches'::regclass)
                    AND NOT EXISTS (SELECT 1 FROM pg_class relation
                        WHERE relation.oid IN ('andril_social.social_directional_interests'::regclass, 'andril_social.social_matches'::regclass)
                            AND pg_has_role(current_user, relation.relowner, 'MEMBER'))) AS "Value"
                FROM pg_roles WHERE rolname = current_user
                """).SingleAsync(token);
            if (!restricted) throw new InvalidOperationException("The Social relationship database role must be subject to row security.");
            if (!await db.Database.SqlQuery<bool>($"SELECT andril_social.questionnaire_owner_visible({actor}) AS \"Value\"").SingleAsync(token))
                throw new ProfileAccessDeniedException();

            var pairIdentity = $"social-relationship:{pair.FirstProfileId:D}:{pair.SecondProfileId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({pairIdentity}, 0))", token);
            var result = await action(db, pair.GetOtherProfileId(actor), token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('role', {previous.Role}, true), set_config('andril.subject', {previous.Subject}, true),
                    set_config('andril.account', {previous.Account}, true), set_config('andril.module', {previous.Module}, true)
                """, token);
            await transaction.ReleaseSavepointAsync(Savepoint, token);
            return result;
        }
        catch (Exception failure)
        {
            await transaction.RollbackToSavepointAsync(Savepoint, CancellationToken.None);
            await transaction.ReleaseSavepointAsync(Savepoint, CancellationToken.None);
            if (failure is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation,
                ConstraintName: "fk_social_interest_actor" or "fk_social_interest_target" or "fk_social_match_first" or "fk_social_match_second" })
                throw new ProfileAccessDeniedException();
            throw;
        }
    }

    private sealed record ReciprocalInterest(bool IsPositive, DateTime RecordedAtUtc);
    private sealed record SessionScope(string Role, string Subject, string Account, string Module);
}
