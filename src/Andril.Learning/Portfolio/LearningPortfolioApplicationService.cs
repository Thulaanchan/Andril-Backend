namespace Andril.Learning.Portfolio;

/// <summary>Unregistered private create/save/reopen orchestration. Repository owns current
/// authorization and coordinated persistence; no endpoints, production authority or autosave.</summary>
public sealed class LearningPortfolioApplicationService(ILearningPortfolioRepository repository)
{
    public Task<PortfolioReadResult> CreateAsync(PortfolioTemplate template, CancellationToken ct = default) => repository.CreateAsync(template, ct);
    public Task<PortfolioReadResult> ReopenAsync(Guid id, CancellationToken ct = default) => repository.OpenAsync(id, ct);
    public Task<PortfolioListResult> ListOwnAsync(CancellationToken ct = default) => repository.ListAsync(ct);
    public Task<PortfolioReadResult> AddProjectAsync(Guid id, PortfolioProject project, CancellationToken ct = default) => repository.SaveAsync(id, PortfolioSaveChange.Add(project), ct);
    public Task<PortfolioReadResult> EditProjectAsync(Guid id, Guid projectId, string? title, string? description,
        IEnumerable<string>? media = null, IEnumerable<string>? links = null, CancellationToken ct = default) => repository.SaveAsync(id, PortfolioSaveChange.Edit(projectId, title, description, media, links), ct);
    public Task<PortfolioReadResult> ReorderProjectsAsync(Guid id, IEnumerable<Guid> projectIds, CancellationToken ct = default) => repository.SaveAsync(id, PortfolioSaveChange.Reorder(projectIds), ct);
    public Task<PortfolioReadResult> SelectTemplateAsync(Guid id, PortfolioTemplate template, CancellationToken ct = default) => repository.SaveAsync(id, PortfolioSaveChange.Template(template), ct);
    public Task<PortfolioDeleteResult> DeleteAsync(Guid id, CancellationToken ct = default) => repository.DeleteAsync(id, ct);
}
