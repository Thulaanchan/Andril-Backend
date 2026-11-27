using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.Social;

public sealed class SocialQuestionnaireCurrentResponseConflictException()
    : InvalidOperationException("A current questionnaire response already exists.");

/// <summary>Stores the questionnaire inside the transaction holding the trusted profile authorization.</summary>
public sealed class PostgresSocialQuestionnaireLifecycleRepository(
    AccountsDbContext accounts,
    DbContextOptions<SocialDbContext> socialOptions,
    ICurrentUser user,
    string runtimeRole) : ISocialQuestionnaireLifecycleRepository
{
    private const string Savepoint = "andril_social_questionnaire";
    private const string CurrentResponseKey = "pk_social_questionnaire_current_responses";

    public Task<QuestionnaireResponseEntity?> GetActiveResponseAsync(Guid profileId, CancellationToken cancellationToken = default)
        => InAuthorizedTransactionAsync<QuestionnaireResponseEntity?>(profileId, async (db, token) =>
        {
            var currentId = await db.Set<SocialQuestionnaireCurrentResponse>().AsNoTracking()
                .Where(row => row.ProfileId == profileId).Select(row => (Guid?)row.ResponseId).SingleOrDefaultAsync(token);
            return currentId is null ? null : await db.QuestionnaireResponses.AsNoTracking().Include(response => response.Answers)
                .SingleAsync(response => response.ProfileId == profileId && response.Id == currentId, token);
        }, cancellationToken);

    public Task CreateInitialResponseAsync(Guid profileId, QuestionnaireResponseEntity initialResponse,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireResponseOwner(profileId, initialResponse);
        return InAuthorizedTransactionAsync(profileId, async (db, token) =>
        {
            if (await db.Set<SocialQuestionnaireCurrentResponse>().AnyAsync(row => row.ProfileId == profileId, token))
                throw new SocialQuestionnaireCurrentResponseConflictException();

            db.QuestionnaireResponses.Add(initialResponse);
            db.Add(new SocialQuestionnaireCurrentResponse(profileId, initialResponse.Id));
            await db.SaveChangesAsync(token);
            return true;
        }, cancellationToken);
    }

    public Task ReplaceCurrentResponseAsync(Guid profileId, Guid existingResponseId, QuestionnaireResponseEntity newResponse,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireResponseOwner(profileId, newResponse);
        return RequireUnsupportedTransitionAsync(profileId, existingResponseId, cancellationToken);
    }

    public Task WithdrawResponseAsync(Guid profileId, Guid responseId, CancellationToken cancellationToken = default)
        => RequireUnsupportedTransitionAsync(profileId, responseId, cancellationToken);

    private Task RequireUnsupportedTransitionAsync(Guid profileId, Guid responseId, CancellationToken cancellationToken)
        => InAuthorizedTransactionAsync<bool>(profileId, async (db, token) =>
        {
            if (responseId == Guid.Empty || !await db.Set<SocialQuestionnaireCurrentResponse>()
                .AnyAsync(row => row.ProfileId == profileId && row.ResponseId == responseId, token))
                throw new ProfileAccessDeniedException();

            // Replacement and withdrawal require approved product behavior for historical responses.
            throw new NotSupportedException("This questionnaire transition is unavailable.");
        }, cancellationToken);

    private static void RequireResponseOwner(Guid profileId, QuestionnaireResponseEntity response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (profileId == Guid.Empty || response.ProfileId != profileId || response.Id == Guid.Empty)
            throw new ProfileAccessDeniedException();
    }

    internal async Task<T> InAuthorizedTransactionAsync<T>(Guid profileId,
        Func<SocialDbContext, CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (profileId == Guid.Empty || !user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            throw new ProfileAccessDeniedException();
        if (string.IsNullOrWhiteSpace(runtimeRole)) throw new InvalidOperationException("The Social database role is required.");
        if (accounts.Database.CurrentTransaction is not { SupportsSavepoints: true } transaction)
            throw new InvalidOperationException("The Social questionnaire requires a current Accounts authorization transaction.");

        var accountId = await accounts.Accounts.AsNoTracking().Where(account => account.SupabaseUserId == subject)
            .Select(account => account.Id).SingleOrDefaultAsync(token);
        if (accountId == Guid.Empty) throw new ProfileAccessDeniedException();

        // M2 holds a lock on the profile row. Joining that transaction also lets the
        // current-response foreign key check the same profile without opening another connection.
        var options = new DbContextOptionsBuilder<SocialDbContext>(socialOptions)
            .UseNpgsql(accounts.Database.GetDbConnection()).Options;
        await using var db = new SocialDbContext(options);
        await db.Database.UseTransactionAsync(transaction.GetDbTransaction(), token);
        await transaction.CreateSavepointAsync(Savepoint, token);

        try
        {
            var previous = await db.Database.SqlQueryRaw<SessionScope>("""
                SELECT current_setting('role') AS "Role",
                       coalesce(current_setting('andril.subject', true), '') AS "Subject",
                       coalesce(current_setting('andril.account', true), '') AS "Account",
                       coalesce(current_setting('andril.module', true), '') AS "Module"
                """).SingleAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('andril.subject', {subject.ToString()}, true),
                       set_config('andril.account', {accountId.ToString()}, true),
                       set_config('andril.module', 'social', true), set_config('role', {runtimeRole}, true)
                """, token);

            var restricted = await db.Database.SqlQueryRaw<bool>("""
                SELECT (current_user <> session_user AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                    AND row_security_active('andril_social.social_questionnaire_current_responses'::regclass)
                    AND row_security_active('andril_social.social_questionnaire_responses'::regclass)
                    AND row_security_active('andril_social.social_questionnaire_answers'::regclass)) AS "Value"
                FROM pg_roles WHERE rolname = current_user
                """).SingleAsync(token);
            if (!restricted) throw new InvalidOperationException("The Social database role must be subject to row security.");
            if (!await db.Database.SqlQuery<bool>($"SELECT andril_social.questionnaire_owner_visible({profileId}) AS \"Value\"").SingleAsync(token))
                throw new ProfileAccessDeniedException();

            var result = await action(db, token);
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
            if (failure is DbUpdateException { InnerException: PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: CurrentResponseKey } })
                throw new SocialQuestionnaireCurrentResponseConflictException();
            throw;
        }
    }

    private sealed class SessionScope
    {
        public string Role { get; init; } = string.Empty;
        public string Subject { get; init; } = string.Empty;
        public string Account { get; init; } = string.Empty;
        public string Module { get; init; } = string.Empty;
    }
}
