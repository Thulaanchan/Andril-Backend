using System.Data;
using System.Globalization;
using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Persistence.Repositories.Diary;

/// <summary>Unregistered local composition of existing module/profile gates and real Diary
/// PostgreSQL persistence. Production Diary-purpose/session/standing adapters and coordinated
/// M2 callbacks are not supplied. Only synthetic, non-network authority is used by the isolated
/// host: a real production profile callback must not be nested across independent transactions.
/// No Learning revision, positive authorization cache, AI consumer or mutation replay is added.</summary>
internal sealed class IsolatedModuleDiaryRepository : IModuleDiaryRepository
{
    private readonly DbContextOptions<DiaryDbContext> options;
    private readonly ICurrentUser user;
    private readonly IPrivateModuleAccess access;
    private readonly IPrivateProfileService profiles;
    private readonly IsolatedDiaryEditingService editing;
    private readonly LearningDiaryTransaction? production;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }

    internal IsolatedModuleDiaryRepository(LearningDiaryTransaction production)
    {
        this.production=production;
        options=null!;user=null!;access=null!;profiles=null!;editing=null!;
    }

    internal IsolatedModuleDiaryRepository(DbContextOptions<DiaryDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles)
    {
        this.options = options; this.user = user; this.access = access; this.profiles = profiles;
        editing = new(access, profiles);
        using var db = Open(); var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Pooling || settings.Database is not { } name
            || !name.StartsWith("andril_m2_test_", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["andril_m2_test_".Length..], "N", out _))
            throw new InvalidOperationException("Diary integration requires an isolated nonpooled local test database.");
    }

    private DiaryDbContext Open() => new(options);
    internal sealed record Owner(Guid Subject, Guid Profile, string Module);

    private async Task<Owner> Resolve(string module, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ModuleNames.IsValid(module)) throw new ArgumentException("Unsupported Diary module.", nameof(module));
        if (!user.IsAuthenticated || user.UserId is not { } subject || subject == Guid.Empty) throw new ProfileAccessDeniedException();
        await access.RequireAsync(module, ct);
        var profile = await profiles.WithAuthorizedProfileAsync(module, p => Task.FromResult(p), ct);
        ct.ThrowIfCancellationRequested();
        if (profile.ProfileId == Guid.Empty || profile.Module != module) throw new ProfileAccessDeniedException();
        return new(subject, profile.ProfileId, module);
    }

    private async Task Revalidate(Owner owner, CancellationToken ct)
    {
        if(production is not null){production.Revalidate(owner,ct);return;}
        var fresh = await Resolve(owner.Module, ct);
        if (fresh != owner) throw new ProfileAccessDeniedException();
    }

    private async Task<T> Within<T>(string module, Func<DiaryDbContext, Owner, CancellationToken, Task<T>> operation,
        Func<Exception, T> expectedFailure, CancellationToken ct, Func<T>? commitUnknown = null)
    {
        if(production is not null)return await production.Run(module,async(db,owner,token)=>
        {
            var result=await operation(db,owner,token);
            await Checkpoint("release",token);await Revalidate(owner,token);return result;
        },expectedFailure,ct,commitUnknown);
        try
        {
            var owner = await Resolve(module, ct);
            await using var db = Open(); await db.Database.OpenConnectionAsync(ct);
            // Exact existing M2 subject key; acquire before a Serializable snapshot. Connection
            // disposal releases the session lock even on cancellation; pooling is prohibited.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({owner.Subject.ToString()},0))", ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan=off; SET LOCAL enable_bitmapscan=off", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{owner.Subject.ToString()},true),set_config('andril.module',{module},true)", ct);
            var account = await db.Database.SqlQuery<Guid>($"SELECT COALESCE(andril_diary.lock_owner({owner.Subject},{owner.Profile},{owner.Module}),'00000000-0000-0000-0000-000000000000'::uuid) AS \"Value\"").SingleAsync(ct);
            if (account == Guid.Empty) throw new ProfileAccessDeniedException();
            await Revalidate(owner, ct);
            var result = await operation(db, owner, ct);
            await Checkpoint("release", ct); await Revalidate(owner, ct); ct.ThrowIfCancellationRequested();
            // Reads and no-op/missing outcomes still close their coordinated transaction.
            try { await tx.CommitAsync(ct); }
            catch (Exception error) when (!Conflict(error) && error is System.Data.Common.DbException or IOException or TimeoutException or OperationCanceledException)
            {
                // Only an actual commit attempt can become unknown. No retry, false success
                // or claim that cancellation rolled back a potentially committed write.
                return commitUnknown is null ? expectedFailure(new IOException("Diary commit outcome unavailable.")) : commitUnknown();
            }
            return result;
        }
        catch (Exception error) when (IsExpected(error)) { return expectedFailure(error); }
    }

    private Task Checkpoint(string phase, CancellationToken ct) => TestCheckpoint?.Invoke(phase, ct) ?? Task.CompletedTask;
    private Task<DiaryEntrySnapshot> Snapshot(ModuleDiaryEntry entry,CancellationToken ct) => production is null
        ? editing.ReadSnapshotAsync(entry,ct):Task.FromResult(production.Snapshot(entry,ct));

    public Task<DiaryReadResult> ReadAsync(string module, DateOnly date, CancellationToken cancellationToken = default) =>
        Within(module, async (db, owner, ct) =>
        {
            var row = await db.Set<DiaryEntryRow>().AsNoTracking().SingleOrDefaultAsync(
                e => e.OwnerModuleProfileId == owner.Profile && e.Module == owner.Module && e.CalendarDate == date, ct);
            if (row is null) return DiaryReadResult.Missing();
            ModuleDiaryEntry entry;
            try { entry = row.ToDomain(); }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            { return DiaryReadResult.Unavailable(); }
            return DiaryReadResult.Available(await Snapshot(entry, ct));
        }, ReadFailure, cancellationToken);

    public Task<DiaryCalendarResult> CalendarAsync(string module, DateOnly from, DateOnly through, CancellationToken cancellationToken = default)
    {
        if (through < from) throw new ArgumentException("Diary calendar range is invalid.");
        return Within(module, async (db, owner, ct) => DiaryCalendarResult.Available(await db.Set<DiaryEntryRow>().AsNoTracking()
            .Where(e => e.OwnerModuleProfileId == owner.Profile && e.Module == owner.Module && e.CalendarDate >= from && e.CalendarDate <= through)
            .OrderBy(e => e.CalendarDate).Select(e => e.CalendarDate).ToArrayAsync(ct)),
            error => Denied(error) ? DiaryCalendarResult.Denied() : DiaryCalendarResult.Unavailable(), cancellationToken);
    }

    public Task<DiaryWriteOutcome> CreateAsync(ModuleDiaryEntry entry, CancellationToken cancellationToken = default) => Write(entry, false, cancellationToken);
    public Task<DiaryWriteOutcome> EditAsync(ModuleDiaryEntry entry, CancellationToken cancellationToken = default) => Write(entry, true, cancellationToken);

    private Task<DiaryWriteOutcome> Write(ModuleDiaryEntry entry, bool edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Mutation(entry.Module, entry.Date, async (db, owner, token) =>
        {
            if (entry.OwnerModuleProfileId != owner.Profile || entry.Module != owner.Module) throw new ProfileAccessDeniedException();
            // B3-03's immutable private snapshot copies the caller's shape under existing owner
            // gates; persistence never serializes the JsonIgnore domain values by accident.
            var replacement = DiaryEntryRow.From(await Snapshot(entry, token));
            if (!edit) db.Set<DiaryEntryRow>().Add(replacement);
            else
            {
                var stored = await LockedRow(db, owner, entry.Date, token);
                if (stored is null) return DiaryWriteOutcome.NotFound;
                stored.ReplaceContent(replacement);
            }
            await db.SaveChangesAsync(token); await Checkpoint("prepared", token);
            return DiaryWriteOutcome.Committed;
        }, ct);
    }

    public Task<DiaryWriteOutcome> DeleteAsync(string module, DateOnly date, CancellationToken cancellationToken = default) =>
        Mutation(module, date, async (db, owner, ct) =>
        {
            var row = await LockedRow(db, owner, date, ct);
            if (row is null) return DiaryWriteOutcome.NotFound;
            db.Remove(row); await db.SaveChangesAsync(ct); await Checkpoint("prepared", ct);
            return DiaryWriteOutcome.Committed;
        }, cancellationToken);

    private static Task<DiaryEntryRow?> LockedRow(DiaryDbContext db, Owner owner, DateOnly date, CancellationToken ct) =>
        db.Set<DiaryEntryRow>().FromSqlInterpolated($"SELECT * FROM andril_diary.module_diary_entries WHERE owner_module_profile_id={owner.Profile} AND module={owner.Module} AND calendar_date=CAST({date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} AS date) FOR UPDATE").SingleOrDefaultAsync(ct);

    private Task<DiaryWriteOutcome> Mutation(string module, DateOnly date,
        Func<DiaryDbContext, Owner, CancellationToken, Task<DiaryWriteOutcome>> mutate, CancellationToken ct)
    {
        _ = date; // Literal DateOnly identity only; never resolve/rekey existing dates here.
        return Within(module, async (db, owner, token) =>
            {
                var result = await mutate(db, owner, token);
                await Checkpoint("before-commit", token); await Revalidate(owner, token); token.ThrowIfCancellationRequested();
                // Within performs the final release check before actually attempting commit.
                return result;
            }, WriteFailure, ct, () => DiaryWriteOutcome.CommitUnknown);
    }

    private static bool Denied(Exception error) => error is ProfileAccessDeniedException or ProfileMissingException;
    private static bool Conflict(Exception error) => error is PostgresException
        { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }
        || error is DbUpdateException { InnerException: PostgresException
            { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } }
        || error is DbUpdateException { InnerException: PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_module_diary_entries" } }
        || error is DiaryTransactionConflictException
        || error is InvalidOperationException { InnerException: DbUpdateException
            { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } } };
    private static bool IsExpected(Exception error) => Denied(error) || Conflict(error)
        || error is ProfileContractPendingException or PhotoDependencyException or NpgsqlException or IOException or TimeoutException;
    private static DiaryReadResult ReadFailure(Exception error) => Denied(error) ? DiaryReadResult.Denied() : DiaryReadResult.Unavailable();
    private static DiaryWriteOutcome WriteFailure(Exception error) => Denied(error) ? DiaryWriteOutcome.Denied
        : Conflict(error) ? DiaryWriteOutcome.Conflict : DiaryWriteOutcome.Unavailable;
}
