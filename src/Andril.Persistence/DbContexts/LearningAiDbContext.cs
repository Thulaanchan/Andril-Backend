using Andril.Persistence.Repositories.AI;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

/// <summary>
/// M1-owned production schema preparation, unregistered. Does not remove isolated repository
/// connection guards or supply authentication, privacy, provider dispatch or cleanup authority.
/// Hosted migration/activation remains separately gated. Apply approved Learning authority
/// migrations before this context: its FORCE RLS policies depend on scope_visible(uuid,text).
/// </summary>
public sealed class LearningAiDbContext(DbContextOptions<LearningAiDbContext> options) : DbContext(options)
{
    public const string Schema = "andril_ai";
    public const string HistoryTable = "__EFMigrationsHistory_AI";
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        IsolatedLearningAiDbContext.ConfigureModel(modelBuilder, Schema);
        // Fixture-only discovery evidence is not a production source of eligibility or AI consent.
        modelBuilder.Ignore<IsolatedClassroomDiscoveryRow>();
        modelBuilder.Ignore<IsolatedLearningPartnerDiscoveryRow>();
    }
}
