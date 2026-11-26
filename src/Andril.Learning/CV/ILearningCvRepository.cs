using System.Text.Json.Serialization;

namespace Andril.Learning.CV;

/// <summary>Private Learning-owned commands. Every operation freshly resolves server-side
/// ownership/current eligibility. IDs identify documents, never an authenticated actor. No
/// public sharing, auto-save, count cap, retention policy or field classification is implied.</summary>
public interface ILearningCvRepository
{
    Task<CvReadResult> CreateAsync(CvTemplate template, CancellationToken cancellationToken = default);
    Task<CvReadResult> OpenAsync(Guid cvId, CancellationToken cancellationToken = default);
    Task<CvListResult> ListAsync(CancellationToken cancellationToken = default);
    Task<CvReadResult> SaveAsync(Guid cvId, CvSaveChange change, CancellationToken cancellationToken = default);
    Task<CvPreviewResult> PreviewAsync(Guid cvId, CancellationToken cancellationToken = default);
}

public enum CvOperationState { Available = 1, NotFound = 2, Denied = 3, Unavailable = 4, Conflict = 5, CommitUnknown = 6 }

public sealed class CvReadResult
{
    [JsonIgnore] public CvOperationState State { get; }
    [JsonIgnore] public LearningCv? Cv { get; }
    private CvReadResult(CvOperationState state, LearningCv? cv) { State = state; Cv = cv; }
    public static CvReadResult Available(LearningCv cv) => new(CvOperationState.Available, cv ?? throw new ArgumentNullException(nameof(cv)));
    public static CvReadResult Failure(CvOperationState state) => state != CvOperationState.Available ? new(state, null) : throw new ArgumentException("An available CV needs content.");
    public override string ToString() => "Private CV result [identity and content omitted]";
}

public sealed class CvListResult
{
    [JsonIgnore] public CvOperationState State { get; }
    [JsonIgnore] public IReadOnlyList<Guid>? CvIds { get; }
    private CvListResult(CvOperationState state, IReadOnlyList<Guid>? ids) { State = state; CvIds = ids; }
    public static CvListResult Available(IEnumerable<Guid> ids) => new(CvOperationState.Available, Array.AsReadOnly(ids.ToArray()));
    public static CvListResult Failure(CvOperationState state) => state != CvOperationState.Available ? new(state, null) : throw new ArgumentException("An available list needs content.");
    public override string ToString() => "Private CV list [identities and counts omitted]";
}

public sealed class CvPreviewResult
{
    [JsonIgnore] public CvOperationState State { get; }
    [JsonIgnore] public CvPreview? Preview { get; }
    private CvPreviewResult(CvOperationState state, CvPreview? preview) { State = state; Preview = preview; }
    public static CvPreviewResult Available(CvPreview preview) => new(CvOperationState.Available, preview ?? throw new ArgumentNullException(nameof(preview)));
    public static CvPreviewResult Failure(CvOperationState state) => state != CvOperationState.Available ? new(state, null) : throw new ArgumentException("An available preview needs content.");
    public override string ToString() => "Private CV preview result [content omitted]";
}

/// <summary>Explicit field-specific save, applied to the current persisted document under its
/// transaction. Unrelated sections/templates/consent are preserved, never replaced implicitly.</summary>
public sealed class CvSaveChange
{
    private readonly Action<LearningCv> apply;
    private CvSaveChange(Action<LearningCv> apply) => this.apply = apply;
    public static CvSaveChange Section(Guid sectionId, CvSectionCategory category, IEnumerable<CvSection.Entry> entries)
    {
        var section = new CvSection(sectionId, category, entries); // Existing validation and immutable copy.
        return new(cv => cv.EditSection(section.Id, section.Category, section.Entries));
    }
    public static CvSaveChange Template(CvTemplate template) => new(cv => cv.SelectTemplate(template));
    public static CvSaveChange ContactConsent(bool include) => new(cv => cv.SetContactInclusionConsent(cv.Id, include));
    public void Apply(LearningCv cv) { ArgumentNullException.ThrowIfNull(cv); apply(cv); }
    public override string ToString() => "Private explicit CV save [values omitted]";
}
