using System.Text.Json.Serialization;

namespace Andril.Learning.CV;

/// <summary>AND-M1-B309-IMPORT-20261126. Document/project IDs select resources, never actors.
/// Each operation needs current trusted Learning ownership and authorization at release/commit.</summary>
public interface ILearningProjectImportRepository
{
    Task<ProjectImportReviewResult> ReviewAsync(Guid portfolioId, Guid cvId, IEnumerable<Guid> projectIds, CancellationToken ct = default);
    Task<ProjectImportConfirmationResult> ConfirmAsync(Guid reviewId, bool confirmed, CancellationToken ct = default);
}

public enum ProjectImportState { Available = 1, Committed, ConfirmationRequired, InvalidSelection, NotFound, Denied, Unavailable, StaleReview, Duplicate, Conflict, CommitUnknown }

/// <summary>Private manual source fields shown for explicit review, never renderable CV fields.</summary>
public sealed class ReviewedPortfolioProject(Guid id, string? title, string? description)
{
    [JsonIgnore] public Guid Id { get; } = id;
    [JsonIgnore] public string? Title { get; } = title;
    [JsonIgnore] public string? Description { get; } = description;
    public override string ToString() => "Private reviewed project [content omitted]";
}

public sealed class ProjectImportReview(Guid id, Guid portfolioId, Guid cvId, IEnumerable<ReviewedPortfolioProject> projects)
{
    [JsonIgnore] public Guid Id { get; } = id;
    [JsonIgnore] public Guid PortfolioId { get; } = portfolioId;
    [JsonIgnore] public Guid CvId { get; } = cvId;
    [JsonIgnore] public IReadOnlyList<ReviewedPortfolioProject> Projects { get; } = Array.AsReadOnly(projects.ToArray());
    public override string ToString() => "Private import review [identities and content omitted]";
}

public sealed class ProjectImportReviewResult
{
    [JsonIgnore] public ProjectImportState State { get; }
    [JsonIgnore] public ProjectImportReview? Review { get; }
    private ProjectImportReviewResult(ProjectImportState state, ProjectImportReview? review) { State = state; Review = review; }
    public static ProjectImportReviewResult Available(ProjectImportReview review) => new(ProjectImportState.Available, review ?? throw new ArgumentNullException(nameof(review)));
    public static ProjectImportReviewResult Failure(ProjectImportState state) => state != ProjectImportState.Available ? new(state, null) : throw new ArgumentException("An available review needs private content.");
    public override string ToString() => "Private import review result [content omitted]";
}

public sealed class ProjectImportConfirmationResult(ProjectImportState state)
{
    [JsonIgnore] public ProjectImportState State { get; } = state;
    public override string ToString() => "Private import confirmation [content omitted]";
}

/// <summary>Unregistered private orchestration. A review token is not an authorization grant.</summary>
public sealed class LearningProjectImportApplicationService(ILearningProjectImportRepository repository)
{
    public Task<ProjectImportReviewResult> ReviewAsync(Guid portfolioId, Guid cvId, IEnumerable<Guid> projectIds, CancellationToken ct = default) => repository.ReviewAsync(portfolioId, cvId, projectIds, ct);
    public Task<ProjectImportConfirmationResult> ConfirmAsync(Guid reviewId, bool confirmed, CancellationToken ct = default) => repository.ConfirmAsync(reviewId, confirmed, ct);
}
