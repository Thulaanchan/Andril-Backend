using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningPrivateActivitiesModelTests
{
    [Fact]
    public void PrivateOwnerModelMatchesItsMigrationSnapshotWithoutConnectingToADatabase()
    {
        using var db=new LearningPrivateActivitiesDbContext(new DbContextOptionsBuilder<LearningPrivateActivitiesDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=model_metadata_only;Username=model_metadata_only").Options);
        var snapshot=db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        static string Shape(Microsoft.EntityFrameworkCore.Metadata.IModel model)=>string.Join(";",model.GetEntityTypes().SelectMany(e=>e.GetProperties()).Select(p=>$"{p.Name}:{p.IsNullable}:{p.ValueGenerated}"));
        Assert.False(db.Database.HasPendingModelChanges(),"Current="+Shape(db.Model)+" Snapshot="+Shape(snapshot));
        Assert.All(db.Model.GetEntityTypes(),entity=>{Assert.Equal("andril_learning",entity.GetSchema());Assert.Equal("private_owner_activities",entity.GetTableName());});
    }
}
