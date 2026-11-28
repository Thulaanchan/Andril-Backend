using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Portfolio;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Owner = Andril.Persistence.Repositories.Learning.IsolatedLearningDocumentBoundary.Owner;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Private Portfolio commands; the ordinary constructor retains its isolated guard.
/// Explicit production composition supplies the real M2 serializable boundary. The shared Learning document
/// boundary owns server identity, lifecycle, Account -> Membership -> Resource locking and
/// fresh authorization at read release/commit. Does not activate production authority.</summary>
internal sealed class IsolatedLearningPortfolioRepository : ILearningPortfolioRepository
{
    private readonly ILearningDocumentBoundary boundary;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }
    internal IsolatedLearningPortfolioRepository(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles) => boundary = new IsolatedLearningDocumentBoundary(options, user, access, profiles);
    internal IsolatedLearningPortfolioRepository(ILearningDocumentBoundary boundary) => this.boundary = boundary;
    private Task<T> Within<T>(Func<LearningDbContext, Owner, CancellationToken, Task<T>> action,
        Func<PortfolioOperationState, T> failure, CancellationToken ct, bool write = false) => boundary.Run(action, f => failure(f switch
        {
            LearningDocumentFailure.Denied => PortfolioOperationState.Denied,
            LearningDocumentFailure.Conflict => PortfolioOperationState.Conflict,
            LearningDocumentFailure.CommitUnknown => PortfolioOperationState.CommitUnknown,
            _ => PortfolioOperationState.Unavailable
        }), ct, write, TestCheckpoint, e => e is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_learning_portfolios" } });
    public Task<PortfolioReadResult> CreateAsync(PortfolioTemplate template, CancellationToken ct = default) => Within(async (db, owner, token) =>
    {
        var portfolio = new LearningPortfolio(Guid.NewGuid(), owner.Profile, template);
        db.Add(LearningPortfolioRow.From(portfolio)); await db.SaveChangesAsync(token); await Prepared(token);
        return PortfolioReadResult.Available(portfolio);
    }, PortfolioReadResult.Failure, ct, true);
    public Task<PortfolioReadResult> OpenAsync(Guid id, CancellationToken ct = default)
    {
        RequireId(id);
        return Within(async (db, owner, token) =>
        {
            var row = await Rows(db, owner).AsNoTracking().SingleOrDefaultAsync(p => p.PortfolioId == id, token);
            return row is null ? PortfolioReadResult.Failure(PortfolioOperationState.NotFound) : Decode(row);
        }, PortfolioReadResult.Failure, ct);
    }
    public Task<PortfolioListResult> ListAsync(CancellationToken ct = default) => Within(async (db, owner, token) =>
        PortfolioListResult.Available(await Rows(db, owner).AsNoTracking().OrderBy(p => p.PortfolioId).Select(p => p.PortfolioId).ToArrayAsync(token)), PortfolioListResult.Failure, ct);
    public Task<PortfolioReadResult> SaveAsync(Guid id, PortfolioSaveChange change, CancellationToken ct = default)
    {
        RequireId(id); ArgumentNullException.ThrowIfNull(change);
        return Within(async (db, owner, token) =>
        {
            var row = await Locked(db, owner, id).SingleOrDefaultAsync(token);
            if (row is null) return PortfolioReadResult.Failure(PortfolioOperationState.NotFound);
            var decoded = Decode(row); if (decoded.Portfolio is null) return decoded;
            change.Apply(decoded.Portfolio); row.ReplaceContent(LearningPortfolioRow.From(decoded.Portfolio));
            await db.SaveChangesAsync(token); await Prepared(token); return decoded;
        }, PortfolioReadResult.Failure, ct, true);
    }
    public Task<PortfolioDeleteResult> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        RequireId(id);
        return Within(async (db, owner, token) =>
        {
            var row = await Locked(db, owner, id).SingleOrDefaultAsync(token);
            if (row is null) return new PortfolioDeleteResult(PortfolioOperationState.NotFound);
            // Explicit user-requested document deletion only; no account lifecycle cascade,
            // project retention policy, background purge or cross-document deletion inferred.
            db.Remove(row); await db.SaveChangesAsync(token); await Prepared(token);
            return new PortfolioDeleteResult(PortfolioOperationState.Available);
        }, s => new PortfolioDeleteResult(s), ct, true);
    }
    public Task<PortfolioPreviewResult> PreviewAsync(Guid id, CancellationToken ct = default)
    {
        RequireId(id);
        return Within(async (db, owner, token) =>
        {
            // Same trusted owner fence and physical row lock as other document operations.
            // Render current persisted content and release only after fresh owner revalidation.
            var row = await db.Set<LearningPortfolioRow>().FromSqlInterpolated($"SELECT * FROM andril_learning.learning_portfolios WHERE owner_learning_profile_id={owner.Profile} AND module='learning' AND portfolio_id={id} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(token);
            if (row is null) return PortfolioPreviewResult.Failure(PortfolioOperationState.NotFound);
            var decoded = Decode(row);
            return decoded.Portfolio is null ? PortfolioPreviewResult.Failure(decoded.State) : PortfolioPreviewResult.Available(PortfolioPreview.CreateOffline(decoded.Portfolio, token));
        }, PortfolioPreviewResult.Failure, ct);
    }
    private Task Prepared(CancellationToken ct) => TestCheckpoint?.Invoke("prepared", ct) ?? Task.CompletedTask;
    private static IQueryable<LearningPortfolioRow> Rows(LearningDbContext db, Owner owner) => db.Set<LearningPortfolioRow>().Where(p => p.OwnerLearningProfileId == owner.Profile && p.Module == "learning");
    private static IQueryable<LearningPortfolioRow> Locked(LearningDbContext db, Owner owner, Guid id) => db.Set<LearningPortfolioRow>().FromSqlInterpolated($"SELECT * FROM andril_learning.learning_portfolios WHERE owner_learning_profile_id={owner.Profile} AND module='learning' AND portfolio_id={id} FOR UPDATE");
    private static PortfolioReadResult Decode(LearningPortfolioRow row)
    {
        try { return PortfolioReadResult.Available(row.ToDomain()); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return PortfolioReadResult.Failure(PortfolioOperationState.Unavailable); }
    }
    private static void RequireId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A Portfolio reference is required.", nameof(id)); }
}
