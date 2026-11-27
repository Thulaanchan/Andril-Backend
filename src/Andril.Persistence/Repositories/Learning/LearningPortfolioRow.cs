using System.Text.Json;
using Andril.Learning.Portfolio;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Learning;

internal sealed class LearningPortfolioRow
{
    public Guid OwnerLearningProfileId { get; private set; }
    public string Module { get; private set; } = "learning";
    public Guid PortfolioId { get; private set; }
    public int Template { get; private set; }
    public string Projects { get; private set; } = null!;
    public Guid ContentVersion { get; private set; } = Guid.NewGuid();
    private sealed record StoredProject(Guid Id, string? Title, string? Description, string[] Media, string[] Links);
    internal static LearningPortfolioRow From(LearningPortfolio p) => new()
    {
        OwnerLearningProfileId = p.OwnerLearningProfileId, PortfolioId = p.Id, Template = (int)p.Template,
        Projects = JsonSerializer.Serialize(p.Projects.Select(x => new StoredProject(x.Id, x.Title, x.Description, x.MediaReferences.ToArray(), x.ExternalLinks.ToArray())))
    };
    internal LearningPortfolio ToDomain()
    {
        var projects = JsonSerializer.Deserialize<StoredProject[]>(Projects) ?? throw new JsonException("Invalid stored Portfolio.");
        if (projects.Any(p => p is null || p.Media is null || p.Links is null)) throw new JsonException("Invalid stored Portfolio.");
        return LearningPortfolio.FromStoredSnapshot(PortfolioId, OwnerLearningProfileId, (PortfolioTemplate)Template,
            projects.Select(p => new PortfolioProject(p.Id, p.Title, p.Description, p.Media, p.Links)));
    }
    internal void ReplaceContent(LearningPortfolioRow row)
    {
        if (PortfolioId != row.PortfolioId || OwnerLearningProfileId != row.OwnerLearningProfileId || Module != row.Module)
            throw new InvalidOperationException("Portfolio identity is immutable.");
        // PostgreSQL jsonb changes whitespace/object-key order. Compare JSON values so an
        // identical explicit save does not advance the durable version merely for formatting.
        if (Template != row.Template || !System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(Projects), System.Text.Json.Nodes.JsonNode.Parse(row.Projects)))
            ContentVersion = Guid.NewGuid();
        Template = row.Template; Projects = row.Projects;
    }
    public override string ToString() => "Private persisted Portfolio [scope and content omitted]";
}

internal static class LearningPortfolioConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var p = model.Entity<LearningPortfolioRow>();
        p.ToTable("learning_portfolios", "andril_learning", t =>
        {
            t.HasCheckConstraint("ck_learning_portfolio_owner", "owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_learning_portfolio_id", "portfolio_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_learning_portfolio_module", "module = 'learning'");
            t.HasCheckConstraint("ck_learning_portfolio_template", "template IN (1,2,3)");
            t.HasCheckConstraint("ck_learning_portfolio_projects", "jsonb_typeof(projects) = 'array'");
            t.HasCheckConstraint("ck_learning_portfolio_version", "content_version <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        p.HasKey(x => new { x.OwnerLearningProfileId, x.Module, x.PortfolioId }).HasName("pk_learning_portfolios");
        p.Property(x => x.OwnerLearningProfileId).HasColumnName("owner_learning_profile_id").ValueGeneratedNever();
        p.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16);
        p.Property(x => x.PortfolioId).HasColumnName("portfolio_id").ValueGeneratedNever();
        p.Property(x => x.Template).HasColumnName("template");
        p.Property(x => x.Projects).HasColumnName("projects").HasColumnType("jsonb").IsRequired();
        p.Property(x => x.ContentVersion).HasColumnName("content_version").ValueGeneratedNever();
    }
}
