using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

/// <summary>Private owner foundations only; no multi-party or discovery authority is implied.</summary>
public sealed class LearningPrivateActivitiesDbContext(DbContextOptions<LearningPrivateActivitiesDbContext> options):DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)=>optionsBuilder.UseNpgsql(
        options=>options.MigrationsHistoryTable("__EFMigrationsHistory_LearningPrivateActivities","andril_learning"));
    protected override void OnModelCreating(ModelBuilder model)=>Configure(model);
    internal static void Configure(ModelBuilder model)
    {
        IsolatedLearningActivitiesDbContext.Configure(model,"andril_learning");
        var row=model.Entity<LearningActivityRow>();
        row.ToTable("private_owner_activities","andril_learning",table=>table.HasCheckConstraint("ck_private_owner_activity",
            "COALESCE((kind IN (3,4) AND jsonb_array_length(members)=1 AND pair_low IS NULL AND pair_high IS NULL AND content->'Members'=members AND content->'Creator'=members->0 AND content->>'Id'=id::text AND content->>'Kind'=kind::text),false)"));
        foreach(var index in row.Metadata.GetIndexes().ToArray())row.Metadata.RemoveIndex(index);
    }
}
