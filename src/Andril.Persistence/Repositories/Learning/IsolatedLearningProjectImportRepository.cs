using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ReviewRow = Andril.Persistence.Repositories.Learning.LearningProjectImportReviewRow;
using OriginRow = Andril.Persistence.Repositories.Learning.LearningProjectImportOriginRow;
using Owner = Andril.Persistence.Repositories.Learning.IsolatedLearningDocumentBoundary.Owner;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Unregistered local IMP-01/02 implementation. Reuses the subject fence and
/// Account/Membership/Profile lock order; all existing Portfolio writers use the same boundary.
/// The import runtime has no source UPDATE grant. Serializable source reads and the existing
/// fence coordinate supported writers; this is not a claim of production M2 coordination.</summary>
internal sealed class IsolatedLearningProjectImportRepository : ILearningProjectImportRepository
{
    private readonly ILearningDocumentBoundary boundary;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }
    internal IsolatedLearningProjectImportRepository(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles) => boundary = new IsolatedLearningDocumentBoundary(options, user, access, profiles);
    internal IsolatedLearningProjectImportRepository(ILearningDocumentBoundary boundary) => this.boundary=boundary;
    private Task<T> Within<T>(Func<LearningDbContext, Owner, CancellationToken, Task<T>> action,
        Func<ProjectImportState, T> failure, CancellationToken ct) => boundary.Run(action, f => failure(f switch
        {
            LearningDocumentFailure.Denied => ProjectImportState.Denied,
            LearningDocumentFailure.Conflict => ProjectImportState.Conflict,
            LearningDocumentFailure.CommitUnknown => ProjectImportState.CommitUnknown,
            _ => ProjectImportState.Unavailable
        }), ct, true, TestCheckpoint, e => e is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_learning_project_import_origins" } });

    public Task<ProjectImportReviewResult> ReviewAsync(Guid portfolioId, Guid cvId, IEnumerable<Guid> projectIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(projectIds); var ids = projectIds.ToArray();
        if (portfolioId == Guid.Empty || cvId == Guid.Empty || ids.Length == 0 || ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Length)
            return Task.FromResult(ProjectImportReviewResult.Failure(ProjectImportState.InvalidSelection));
        return Within(async (db, owner, token) =>
        {
            var source = await Source(db, owner, portfolioId).AsNoTracking().SingleOrDefaultAsync(token);
            var target = await db.Set<LearningCvRow>().AsNoTracking().SingleOrDefaultAsync(x => x.OwnerLearningProfileId == owner.Profile && x.Module == "learning" && x.CvId == cvId, token);
            if (source is null || target is null) return ProjectImportReviewResult.Failure(ProjectImportState.NotFound);
            ReviewRow.SelectedProject[] selected;
            try
            {
                var projects = source.ToDomain().Projects.ToDictionary(x => x.Id); _ = target.ToDomain();
                if (ids.Any(x => !projects.ContainsKey(x))) return ProjectImportReviewResult.Failure(ProjectImportState.InvalidSelection);
                selected = ids.Select(x => new ReviewRow.SelectedProject(x, projects[x].Title, projects[x].Description)).ToArray();
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return ProjectImportReviewResult.Failure(ProjectImportState.Unavailable); }
            var row = new ReviewRow { OwnerLearningProfileId = owner.Profile, ReviewId = Guid.NewGuid(), PortfolioId = portfolioId, CvId = cvId,
                SourceVersion = source.ContentVersion, SourceFingerprint = Fingerprint(source), Projects = JsonSerializer.Serialize(selected) };
            db.Add(row); await db.SaveChangesAsync(token); await Prepared(token); return ProjectImportReviewResult.Available(row.ToDomain());
        }, ProjectImportReviewResult.Failure, ct);
    }

    public Task<ProjectImportConfirmationResult> ConfirmAsync(Guid reviewId, bool confirmed, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!confirmed) return Task.FromResult(new ProjectImportConfirmationResult(ProjectImportState.ConfirmationRequired));
        if (reviewId == Guid.Empty) return Task.FromResult(new ProjectImportConfirmationResult(ProjectImportState.InvalidSelection));
        return Within(async (db, owner, token) =>
        {
            var review = await db.Set<ReviewRow>().SingleOrDefaultAsync(x => x.OwnerLearningProfileId == owner.Profile && x.Module == "learning" && x.ReviewId == reviewId, token);
            if (review is null) return new ProjectImportConfirmationResult(ProjectImportState.NotFound);
            var source = await Source(db, owner, review.PortfolioId).AsNoTracking().SingleOrDefaultAsync(token);
            var target = await db.Set<LearningCvRow>().FromSqlInterpolated($"SELECT * FROM andril_learning.learning_cvs WHERE owner_learning_profile_id={owner.Profile} AND module='learning' AND cv_id={review.CvId} FOR UPDATE").SingleOrDefaultAsync(token);
            if (source is null || target is null) return new ProjectImportConfirmationResult(ProjectImportState.NotFound);
            ReviewRow.SelectedProject[] selected; LearningCv cv;
            try { selected = review.Selected(); _ = source.ToDomain(); cv = target.ToDomain(); }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return new ProjectImportConfirmationResult(ProjectImportState.Unavailable); }
            var ids = selected.Select(x => x.Id).ToArray();
            if (review.Confirmed || await db.Set<OriginRow>().AnyAsync(x => x.OwnerLearningProfileId == owner.Profile && x.Module == "learning" && x.PortfolioId == review.PortfolioId && x.CvId == review.CvId && ids.Contains(x.ProjectId), token))
                return new ProjectImportConfirmationResult(ProjectImportState.Duplicate);
            if (source.ContentVersion != review.SourceVersion || Fingerprint(source) != review.SourceFingerprint)
                return new ProjectImportConfirmationResult(ProjectImportState.StaleReview);
            var section = cv.Sections.Single(x => x.Category == CvSectionCategory.Projects);
            var entries = section.Entries.ToList();
            foreach (var p in selected)
            {
                token.ThrowIfCancellationRequested(); var entryId = Guid.NewGuid();
                entries.Add(new(entryId, section.Id, "Title: " + p.Title + "\nDescription: " + p.Description));
                db.Add(new OriginRow { OwnerLearningProfileId = owner.Profile, PortfolioId = review.PortfolioId, ProjectId = p.Id, CvId = review.CvId, EntryId = entryId, ReviewId = reviewId });
            }
            cv.EditSection(section.Id, section.Category, entries); target.ReplaceContent(LearningCvRow.From(cv)); review.Confirmed = true;
            await db.SaveChangesAsync(token); await Prepared(token); return new ProjectImportConfirmationResult(ProjectImportState.Committed);
        }, s => new ProjectImportConfirmationResult(s), ct);
    }
    private Task Prepared(CancellationToken ct) => TestCheckpoint?.Invoke("prepared", ct) ?? Task.CompletedTask;
    private static IQueryable<LearningPortfolioRow> Source(LearningDbContext db, Owner owner, Guid id) => db.Set<LearningPortfolioRow>().Where(x => x.OwnerLearningProfileId == owner.Profile && x.Module == "learning" && x.PortfolioId == id);
    private static string Fingerprint(LearningPortfolioRow source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Template + "\n" + source.Projects)));
}
