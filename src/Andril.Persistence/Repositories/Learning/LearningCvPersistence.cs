using System.Data;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Owner = Andril.Persistence.Repositories.Learning.IsolatedLearningDocumentBoundary.Owner;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Explicit private-owner CV composition. Requires the real M2 serializable
/// callback on the same scoped Accounts context. No migrations, sharing or AI grants.</summary>
public static class LearningCvPersistence
{
    public static ILearningCvRepository Create(AccountsDbContext accounts, ICurrentUser user,
        IPrivateProfileService profiles, string runtimeRole) =>
        new IsolatedLearningCvRepository(new LearningOwnerDocumentTransaction(accounts, user, profiles, runtimeRole));
}

internal enum LearningPrivateDocument { Cv, Portfolio, Diary, OwnerActivities, XpRead, PeerActivities, Publications }

internal sealed class LearningOwnerDocumentTransaction(AccountsDbContext accounts, ICurrentUser user,
    IPrivateProfileService profiles, string runtimeRole, LearningPrivateDocument document = LearningPrivateDocument.Cv) : ILearningDocumentBoundary
{
    public async Task<T> Run<T>(Func<LearningDbContext, Owner, CancellationToken, Task<T>> action,
        Func<LearningDocumentFailure, T> failure, CancellationToken ct, bool write = false,
        Func<string, CancellationToken, Task>? checkpoint = null, Func<Exception, bool>? additionalConflict = null)
    {
        ct.ThrowIfCancellationRequested();
        // Fixed backend-owned resource scopes; no client-controlled SQL identifier or purpose.
        var table = document switch
        {
            LearningPrivateDocument.Cv => "andril_learning.learning_cvs",
            LearningPrivateDocument.Portfolio => "andril_learning.learning_portfolios",
            LearningPrivateDocument.Diary => "andril_diary.module_diary_entries",
            LearningPrivateDocument.OwnerActivities => "andril_learning.private_owner_activities",
            LearningPrivateDocument.XpRead => "andril_learning.xp_credits",
            LearningPrivateDocument.PeerActivities => "andril_learning.peer_activities",
            LearningPrivateDocument.Publications => "andril_learning.publications",
            _ => throw new ArgumentOutOfRangeException(nameof(document))
        };
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            return failure(LearningDocumentFailure.Denied);
        if (string.IsNullOrWhiteSpace(runtimeRole) || accounts.Database.CurrentTransaction is not null)
            return failure(LearningDocumentFailure.Unavailable);
        bool prepared = false, fenced = false;
        var connection = (NpgsqlConnection)accounts.Database.GetDbConnection();
        bool opened = connection.State != ConnectionState.Open;
        try
        {
            if (opened) await accounts.Database.OpenConnectionAsync(ct);
            // Same existing M2 subject key, before its transaction/snapshot and account locks.
            await accounts.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({subject.ToString()},0))", ct);
            fenced = true;
            return await profiles.WithSerializableLearningProfileAsync(async profile =>
            {
                if (profile.Module != "learning" || profile.ProfileId == Guid.Empty || !user.IsAuthenticated || user.UserId != subject)
                    throw new ProfileAccessDeniedException();
                var tx = accounts.Database.CurrentTransaction;
                if (tx is not { SupportsSavepoints: true } || tx.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
                    throw new ProfileContractPendingException();
                await using var db = new LearningDbContext(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(connection).Options);
                await db.Database.UseTransactionAsync(tx.GetDbTransaction(), ct);
                const string savepoint = "learning_private_cv";
                await tx.CreateSavepointAsync(savepoint, ct);
                try
                {
                    var previous = await db.Database.SqlQueryRaw<SessionScope>("""
                        SELECT current_setting('role') AS "Role", coalesce(current_setting('andril.subject',true),'') AS "Subject",
                            coalesce(current_setting('andril.account',true),'') AS "Account",coalesce(current_setting('andril.module',true),'') AS "Module"
                        """).SingleAsync(ct);
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{runtimeRole},true)", ct);
                    var restricted = await db.Database.SqlQuery<bool>($"""
                        SELECT (current_user<>session_user AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole
                            AND row_security_active(to_regclass({table}))
                            AND (SELECT relforcerowsecurity FROM pg_class WHERE oid=to_regclass({table}))
                            AND EXISTS(SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                                WHERE n.nspname='andril_identity' AND c.relname='account_module_lifecycles'
                                    AND c.relrowsecurity AND c.relforcerowsecurity)
                            AND NOT EXISTS(SELECT FROM pg_class c WHERE c.oid=to_regclass({table})
                                AND pg_has_role(current_user,c.relowner,'MEMBER'))
                            AND NOT EXISTS(SELECT FROM pg_proc p JOIN pg_roles o ON o.oid=p.proowner
                                WHERE (p.oid IN (to_regprocedure('andril_learning.lock_cv_owner(uuid,uuid)'),
                                    to_regprocedure('andril_learning.lock_learning_owners(uuid,uuid,uuid)'),
                                    to_regprocedure('andril_learning.scope_visible(uuid,text)'))
                                    OR p.oid=CASE WHEN {document==LearningPrivateDocument.Diary}
                                        THEN to_regprocedure('andril_diary.scope_visible(uuid,text)') ELSE NULL END)
                                    AND (o.rolsuper OR o.rolbypassrls))
                            AND andril_learning.lock_cv_owner({subject},{profile.ProfileId})) AS "Value"
                        FROM pg_roles WHERE rolname=current_user
                        """).SingleAsync(ct);
                    if (!restricted) throw new ProfileAccessDeniedException();
                    var result = await action(db, new(subject, profile.ProfileId), ct);
                    if (checkpoint is not null) await checkpoint(write ? "before-commit" : "read-release", ct);
                    ct.ThrowIfCancellationRequested();
                    if (!user.IsAuthenticated || user.UserId != subject) throw new ProfileAccessDeniedException();
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        SELECT set_config('role',{previous.Role},true),set_config('andril.subject',{previous.Subject},true),
                            set_config('andril.account',{previous.Account},true),set_config('andril.module',{previous.Module},true)
                        """, ct);
                    await tx.ReleaseSavepointAsync(savepoint, ct);
                    prepared = true;
                    return result;
                }
                catch
                {
                    await tx.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                    await tx.ReleaseSavepointAsync(savepoint, CancellationToken.None);
                    throw;
                }
            }, ct);
        }
        catch (Exception e) when (e is ProfileAccessDeniedException or ProfileMissingException or AccountAuthenticationRequiredException)
        { return failure(LearningDocumentFailure.Denied); }
        catch (Exception e) when (IsolatedLearningPreferencesStore.IsConflict(e) || (additionalConflict?.Invoke(e) ?? false))
        { return failure(write ? LearningDocumentFailure.Conflict : LearningDocumentFailure.Unavailable); }
        catch (OperationCanceledException) when (prepared && write)
        { return failure(LearningDocumentFailure.CommitUnknown); }
        catch (Exception e) when (e is AccountStoreUnavailableException or ProfileContractPendingException or PhotoDependencyException
            or NpgsqlException or IOException or TimeoutException)
        { return failure(prepared && write ? LearningDocumentFailure.CommitUnknown : LearningDocumentFailure.Unavailable); }
        finally
        {
            if (fenced && connection.State == ConnectionState.Open)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await accounts.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtextextended({subject.ToString()},0))", cleanup.Token);
                }
                catch
                {
                    // A pooled connection with an unreleased session fence must never be reused.
                    NpgsqlConnection.ClearPool(connection);
                    await connection.CloseAsync();
                }
            }
            if (opened) await accounts.Database.CloseConnectionAsync();
        }
    }

    private sealed record SessionScope(string Role, string Subject, string Account, string Module);
}
