using System.Text.Json.Serialization;

namespace Andril.Learning.Portfolio;

/// <summary>Private Learning-owned operations. Fresh trusted server identity/current
/// eligibility are required on every call and at commit/read release. A document reference
/// is never an owner or access grant. No publication, media retrieval or retention policy.</summary>
public interface ILearningPortfolioRepository
{
    Task<PortfolioReadResult> CreateAsync(PortfolioTemplate template, CancellationToken ct = default);
    Task<PortfolioReadResult> OpenAsync(Guid id, CancellationToken ct = default);
    Task<PortfolioListResult> ListAsync(CancellationToken ct = default);
    Task<PortfolioReadResult> SaveAsync(Guid id, PortfolioSaveChange change, CancellationToken ct = default);
    Task<PortfolioDeleteResult> DeleteAsync(Guid id, CancellationToken ct = default);
}

public enum PortfolioOperationState { Available = 1, NotFound, Denied, Unavailable, Conflict, CommitUnknown }

public sealed class PortfolioReadResult
{
    [JsonIgnore] public PortfolioOperationState State { get; }
    [JsonIgnore] public LearningPortfolio? Portfolio { get; }
    private PortfolioReadResult(PortfolioOperationState state, LearningPortfolio? portfolio) { State = state; Portfolio = portfolio; }
    public static PortfolioReadResult Available(LearningPortfolio portfolio) => new(PortfolioOperationState.Available, portfolio ?? throw new ArgumentNullException(nameof(portfolio)));
    public static PortfolioReadResult Failure(PortfolioOperationState state) => state != PortfolioOperationState.Available ? new(state, null) : throw new ArgumentException("Available requires private content.");
    public override string ToString() => "Private Portfolio result [identity and content omitted]";
}

public sealed class PortfolioListResult
{
    [JsonIgnore] public PortfolioOperationState State { get; }
    [JsonIgnore] public IReadOnlyList<Guid>? Ids { get; }
    private PortfolioListResult(PortfolioOperationState state, IReadOnlyList<Guid>? ids) { State = state; Ids = ids; }
    public static PortfolioListResult Available(IEnumerable<Guid> ids) => new(PortfolioOperationState.Available, Array.AsReadOnly(ids.ToArray()));
    public static PortfolioListResult Failure(PortfolioOperationState state) => state != PortfolioOperationState.Available ? new(state, null) : throw new ArgumentException("Available requires private content.");
    public override string ToString() => "Private Portfolio list [identities omitted]";
}

public sealed class PortfolioDeleteResult
{
    [JsonIgnore] public PortfolioOperationState State { get; }
    public PortfolioDeleteResult(PortfolioOperationState state) => State = Enum.IsDefined(state) ? state : throw new ArgumentException("Unsupported outcome.");
    public override string ToString() => "Private Portfolio deletion [identity omitted]";
}

/// <summary>Immutable explicit commands applied to the current locked stored document.
/// Reuses existing domain validation and project identities/order. No stale whole-document
/// replacement, autosave, project deletion policy or caller-supplied actor is introduced.</summary>
public sealed class PortfolioSaveChange
{
    private readonly Action<LearningPortfolio> apply;
    private PortfolioSaveChange(Action<LearningPortfolio> apply) => this.apply = apply;
    public static PortfolioSaveChange Add(PortfolioProject project)
    { ArgumentNullException.ThrowIfNull(project); return new(p => p.AddProject(project)); }
    public static PortfolioSaveChange Edit(Guid id, string? title, string? description,
        IEnumerable<string>? media = null, IEnumerable<string>? links = null)
    {
        var copy = new PortfolioProject(id, title, description, media, links);
        return new(p => p.EditProject(copy.Id, copy.Title, copy.Description, copy.MediaReferences, copy.ExternalLinks));
    }
    public static PortfolioSaveChange Reorder(IEnumerable<Guid> ids)
    { ArgumentNullException.ThrowIfNull(ids); var copy = ids.ToArray(); return new(p => p.ReorderProjects(copy)); }
    public static PortfolioSaveChange Template(PortfolioTemplate template) => new(p => p.SelectTemplate(template));
    public void Apply(LearningPortfolio portfolio) { ArgumentNullException.ThrowIfNull(portfolio); apply(portfolio); }
    public override string ToString() => "Private Portfolio change [values omitted]";
}
