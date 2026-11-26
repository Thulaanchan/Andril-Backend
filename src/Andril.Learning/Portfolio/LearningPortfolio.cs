using System.Text.Json.Serialization;

namespace Andril.Learning.Portfolio;

/// <summary>
/// LRN-06 / DEC-20261120-LRN06-01: private, Learning-only in-memory Portfolio editing.
/// A supplied owner ID is not authentication or proof of ownership. M2 must freshly
/// authorize creation, loading and editing against the server-resolved Learning profile.
/// This domain supplies no persistence, publishing, rendering, export, CV import,
/// media retrieval, deletion, XP, connection or communication permissions.
/// </summary>
public sealed class LearningPortfolio
{
    public Guid Id { get; }
    [JsonIgnore]
    public Guid OwnerLearningProfileId { get; }
    public string Module => "learning";
    public bool IsPrivate => true;
    public PortfolioTemplate Template { get; private set; }
    [JsonIgnore]
    public IReadOnlyList<PortfolioProject> Projects { get; private set; }

    public LearningPortfolio(Guid id, Guid ownerLearningProfileId, PortfolioTemplate template)
    {
        if (id == Guid.Empty) throw new ArgumentException("A Portfolio ID is required.", nameof(id));
        if (ownerLearningProfileId == Guid.Empty)
            throw new ArgumentException("A Learning-profile owner ID is required.", nameof(ownerLearningProfileId));
        RequireTemplate(template);
        Id = id;
        OwnerLearningProfileId = ownerLearningProfileId;
        Template = template;
        Projects = Array.AsReadOnly(Array.Empty<PortfolioProject>());
    }

    /// <summary>Validate and restore a private stored snapshot without granting authorization.</summary>
    public static LearningPortfolio FromStoredSnapshot(Guid id, Guid ownerLearningProfileId,
        PortfolioTemplate template, IEnumerable<PortfolioProject> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var portfolio = new LearningPortfolio(id, ownerLearningProfileId, template);
        foreach (var project in projects) portfolio.AddProject(project);
        return portfolio;
    }

    /// <summary>Add an explicitly supplied immutable project; no external content is imported.</summary>
    public void AddProject(PortfolioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (Projects.Any(existing => existing.Id == project.Id))
            throw new ArgumentException("The project identity already exists in this Portfolio.", nameof(project));
        var next = Projects.Append(project).ToArray();
        Projects = Array.AsReadOnly(next);
    }

    /// <summary>Replace an existing project's manual content through its domain contract.
    /// Validation and enumeration finish before the current snapshot is replaced.</summary>
    public void EditProject(Guid projectId, string? title, string? description,
        IEnumerable<string>? mediaReferences = null, IEnumerable<string>? externalLinks = null)
    {
        RequireProjectId(projectId);
        var index = -1;
        for (var i = 0; i < Projects.Count; i++)
            if (Projects[i].Id == projectId) { index = i; break; }
        if (index < 0)
            throw new ArgumentException("The project does not belong to this Portfolio.", nameof(projectId));
        var replacement = Projects[index].WithContent(title, description, mediaReferences, externalLinks);
        var next = Projects.ToArray();
        next[index] = replacement;
        Projects = Array.AsReadOnly(next);
    }

    /// <summary>Accept an explicit complete permutation of the existing project identities.
    /// Only positions change; the same immutable project objects retain all content.
    /// Invalid or incomplete permutations leave the entire Portfolio unchanged.</summary>
    public void ReorderProjects(IEnumerable<Guid> projectIds)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        var order = projectIds.ToArray();
        if (order.Length != Projects.Count)
            throw new ArgumentException("The order must include every existing project exactly once.", nameof(projectIds));
        var existing = Projects.ToDictionary(project => project.Id);
        var seen = new HashSet<Guid>();
        var next = new PortfolioProject[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            if (order[i] == Guid.Empty || !seen.Add(order[i]) || !existing.TryGetValue(order[i], out var project))
                throw new ArgumentException("The order must contain distinct existing project identities.", nameof(projectIds));
            next[i] = project;
        }
        Projects = Array.AsReadOnly(next);
    }

    /// <summary>Change template selection only; project content and order are preserved.</summary>
    public void SelectTemplate(PortfolioTemplate template)
    {
        RequireTemplate(template);
        Template = template;
    }

    private static void RequireTemplate(PortfolioTemplate template)
    {
        if (!Enum.IsDefined(template))
            throw new ArgumentException("Unsupported Portfolio template.", nameof(template));
    }

    private static void RequireProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("A Portfolio project ID is required.", nameof(projectId));
    }

    public override string ToString() => "Private Learning Portfolio [identity, owner and content omitted]";
}
