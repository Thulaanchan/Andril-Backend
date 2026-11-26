namespace Andril.Learning.CV;

/// <summary>Unregistered explicit save/reopen orchestration. The repository owns fresh owner
/// authorization, atomicity and current persisted consent at preview release. No auto-save or
/// client profile scope. Existing generic text remains unclassified and is not preview output.</summary>
public sealed class LearningCvApplicationService(ILearningCvRepository repository)
{
    public Task<CvReadResult> CreateAsync(CvTemplate template, CancellationToken ct = default) => repository.CreateAsync(template, ct);
    public Task<CvReadResult> ReopenAsync(Guid id, CancellationToken ct = default) => repository.OpenAsync(id, ct);
    public Task<CvListResult> ListOwnAsync(CancellationToken ct = default) => repository.ListAsync(ct);
    public Task<CvReadResult> SaveSectionAsync(Guid id, Guid sectionId, CvSectionCategory category, IEnumerable<CvSection.Entry> entries, CancellationToken ct = default) =>
        repository.SaveAsync(id, CvSaveChange.Section(sectionId, category, entries), ct);
    public Task<CvReadResult> SelectTemplateAsync(Guid id, CvTemplate template, CancellationToken ct = default) => repository.SaveAsync(id, CvSaveChange.Template(template), ct);
    public Task<CvReadResult> SetContactConsentAsync(Guid id, bool include, CancellationToken ct = default) => repository.SaveAsync(id, CvSaveChange.ContactConsent(include), ct);
    public Task<CvPreviewResult> PreviewAsync(Guid id, CancellationToken ct = default) => repository.PreviewAsync(id, ct);
    public Task<CvPdfResult> ExportPdfAsync(Guid id, CancellationToken ct = default) => repository.ExportPdfAsync(id, ct);
}
