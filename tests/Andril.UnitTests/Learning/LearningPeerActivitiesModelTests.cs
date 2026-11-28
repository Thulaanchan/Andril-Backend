using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningPeerActivitiesModelTests
{
    [Fact]
    public void PeerModelMatchesItsVersionedSnapshotWithoutDatabaseAccess()
    {
        using var db=new LearningPeerActivitiesDbContext(new DbContextOptionsBuilder<LearningPeerActivitiesDbContext>().UseNpgsql("Host=127.0.0.1;Database=model_metadata_only;Username=model_metadata_only").Options);
        static string Shape(Microsoft.EntityFrameworkCore.Metadata.IModel model)=>string.Join(";",model.GetEntityTypes().SelectMany(e=>e.GetProperties()).Select(p=>$"{p.Name}:{p.IsNullable}:{p.ValueGenerated}"));
        var snapshot=db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        Assert.False(db.Database.HasPendingModelChanges(),"Current="+Shape(db.Model)+" Snapshot="+Shape(snapshot));
        var entity=Assert.Single(db.Model.GetEntityTypes());Assert.Equal("andril_learning",entity.GetSchema());Assert.Equal("peer_activities",entity.GetTableName());
    }
}
