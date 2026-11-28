using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningPublicationsModelTests
{
    [Fact]
    public void ExplicitPublicationModelMatchesVersionedSnapshotWithoutDatabaseAccess()
    {
        using var db=new LearningPublicationsDbContext(new DbContextOptionsBuilder<LearningPublicationsDbContext>().UseNpgsql("Host=127.0.0.1;Database=model_metadata_only;Username=model_metadata_only").Options);
        Assert.False(db.Database.HasPendingModelChanges());var entity=Assert.Single(db.Model.GetEntityTypes());Assert.Equal("andril_learning",entity.GetSchema());Assert.Equal("publications",entity.GetTableName());
    }
}
