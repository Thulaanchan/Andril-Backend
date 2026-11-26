using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Owner = Andril.Persistence.Repositories.Learning.IsolatedLearningDocumentBoundary.Owner;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Unregistered CV-only local composition. Reuses Common module/profile gates and
/// the established Learning physical owner lock function, not LearningSetup operation grants.
/// Production CV-purpose/session/standing adapters and transaction-bound M2 callbacks remain
/// pending. Synthetic non-network Common authorities are used only in the isolated test host.</summary>
internal sealed class IsolatedLearningCvRepository : ILearningCvRepository
{
    private readonly IsolatedLearningDocumentBoundary boundary;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }

    internal IsolatedLearningCvRepository(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles)
        => boundary = new(options, user, access, profiles);

    private Task<T> Within<T>(Func<LearningDbContext, Owner, CancellationToken, Task<T>> action,
        Func<CvOperationState, T> failure, CancellationToken ct, bool write = false)
        => boundary.Run(action, f => failure(f switch
        {
            LearningDocumentFailure.Denied => CvOperationState.Denied,
            LearningDocumentFailure.Conflict => CvOperationState.Conflict,
            LearningDocumentFailure.CommitUnknown => CvOperationState.CommitUnknown,
            _ => CvOperationState.Unavailable
        }), ct, write, TestCheckpoint, Conflict);

    public Task<CvReadResult> CreateAsync(CvTemplate template, CancellationToken cancellationToken = default) => Within(async (db, owner, ct) =>
    {
        var cv = new LearningCv(Guid.NewGuid(), owner.Profile, template);
        db.Add(LearningCvRow.From(cv)); await db.SaveChangesAsync(ct); await Prepared(ct);
        return CvReadResult.Available(cv);
    }, CvReadResult.Failure, cancellationToken, true);

    public Task<CvReadResult> OpenAsync(Guid cvId, CancellationToken cancellationToken = default)
    {
        RequireId(cvId);
        return Within(async (db, owner, ct) =>
        {
            var row = await Rows(db, owner).AsNoTracking().SingleOrDefaultAsync(c => c.CvId == cvId, ct);
            if (row is null) return CvReadResult.Failure(CvOperationState.NotFound);
            return Decode(row);
        }, CvReadResult.Failure, cancellationToken);
    }
    public Task<CvListResult> ListAsync(CancellationToken cancellationToken = default) => Within(async (db, owner, ct) =>
        CvListResult.Available(await Rows(db, owner).AsNoTracking().OrderBy(c => c.CvId).Select(c => c.CvId).ToArrayAsync(ct)), CvListResult.Failure, cancellationToken);

    public Task<CvReadResult> SaveAsync(Guid cvId, CvSaveChange change, CancellationToken cancellationToken = default)
    {
        RequireId(cvId); ArgumentNullException.ThrowIfNull(change);
        return Within(async (db, owner, ct) =>
        {
            var row = await db.Set<LearningCvRow>().FromSqlInterpolated($"SELECT * FROM andril_learning.learning_cvs WHERE owner_learning_profile_id={owner.Profile} AND module='learning' AND cv_id={cvId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (row is null) return CvReadResult.Failure(CvOperationState.NotFound);
            var decoded = Decode(row); if (decoded.Cv is null) return decoded;
            change.Apply(decoded.Cv); row.ReplaceContent(LearningCvRow.From(decoded.Cv));
            await db.SaveChangesAsync(ct); await Prepared(ct);
            return CvReadResult.Available(decoded.Cv);
        }, CvReadResult.Failure, cancellationToken, true);
    }

    public Task<CvPreviewResult> PreviewAsync(Guid cvId, CancellationToken cancellationToken = default)
    {
        RequireId(cvId);
        return Within(async (db, owner, ct) =>
        {
            var row = await Rows(db, owner).AsNoTracking().SingleOrDefaultAsync(c => c.CvId == cvId, ct);
            if (row is null) return CvPreviewResult.Failure(CvOperationState.NotFound);
            var decoded = Decode(row);
            // Re-read current persisted choice, never accept a stale caller-held CV as consent.
            // Generic text is unclassified: existing safe preview withholds it even if true.
            return decoded.Cv is null ? CvPreviewResult.Failure(decoded.State) : CvPreviewResult.Available(CvPreview.CreateOffline(decoded.Cv));
        }, CvPreviewResult.Failure, cancellationToken);
    }
    public Task<CvPdfResult> ExportPdfAsync(Guid cvId, CancellationToken cancellationToken = default)
    {
        RequireId(cvId);
        return Within(async (db, owner, ct) =>
        {
            // Keep persisted document/consent stable until the coordinated release. All normal
            // saves also use the existing subject fence; no new production lock namespace.
            var row = await db.Set<LearningCvRow>().FromSqlInterpolated($"SELECT * FROM andril_learning.learning_cvs WHERE owner_learning_profile_id={owner.Profile} AND module='learning' AND cv_id={cvId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null) return CvPdfResult.Failure(CvOperationState.NotFound);
            var decoded = Decode(row);
            return decoded.Cv is null ? CvPdfResult.Failure(decoded.State) : CvPdfResult.Available(CvPdfDocument.CreateOffline(decoded.Cv, ct));
        }, CvPdfResult.Failure, cancellationToken);
    }
    private Task Prepared(CancellationToken ct) => TestCheckpoint?.Invoke("prepared", ct) ?? Task.CompletedTask;
    private static IQueryable<LearningCvRow> Rows(LearningDbContext db, Owner owner) => db.Set<LearningCvRow>().Where(c => c.OwnerLearningProfileId == owner.Profile && c.Module == "learning");
    private static CvReadResult Decode(LearningCvRow row)
    {
        try { return CvReadResult.Available(row.ToDomain()); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { return CvReadResult.Failure(CvOperationState.Unavailable); }
    }
    private static void RequireId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A CV reference is required.", nameof(id)); }
    private static bool Conflict(Exception e) => IsolatedLearningPreferencesStore.IsConflict(e)
        || e is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_learning_cvs" } };
}
