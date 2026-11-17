
using System;
using System.Linq;
using System.Reflection;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

public sealed class NotificationPersistenceMappingTests
{
    private static IMutableEntityType BuildMappedEntity()
    {
        var assembly = typeof(AccountsDbContext).Assembly;

        var recordType = assembly.GetType(
            "Andril.Persistence.Repositories.Communication.NotificationPersistenceRecord",
            throwOnError: true)!;

        var configurationType = assembly.GetType(
            "Andril.Persistence.Configurations.NotificationPersistenceRecordConfiguration",
            throwOnError: true)!;

        var configuration = Activator.CreateInstance(
            configurationType,
            nonPublic: true)!;

        // Inspect the proposed mapping without creating a
        // PostgreSQL database or applying migrations.
        var modelBuilder = new ModelBuilder(
            new ConventionSet());

        var applyConfiguration = typeof(ModelBuilder)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method =>
                method.Name == nameof(ModelBuilder.ApplyConfiguration)
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1
                && method.GetParameters().Length == 1);

        applyConfiguration
            .MakeGenericMethod(recordType)
            .Invoke(modelBuilder, new[] { configuration });

        return modelBuilder.Model.FindEntityType(recordType)
            ?? throw new InvalidOperationException(
                "Notification persistence entity was not mapped.");
    }

    [Fact]
    public void NotificationId_ShouldBePrimaryKey()
    {
        var entity = BuildMappedEntity();

        var key = entity.FindPrimaryKey();

        Assert.NotNull(key);
        Assert.Equal(
            new[] { "Id" },
            key.Properties.Select(p => p.Name).ToArray());

        Assert.Equal(
            ValueGenerated.Never,
            entity.FindProperty("Id")!.ValueGenerated);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("RecipientModuleProfileId")]
    [InlineData("Module")]
    [InlineData("Type")]
    [InlineData("Title")]
    [InlineData("CreatedAtUtc")]
    public void RequiredProperties_ShouldNotBeNullable(
        string propertyName)
    {
        var entity = BuildMappedEntity();

        var property = entity.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.False(property.IsNullable);
    }

    [Theory]
    [InlineData("Preview")]
    [InlineData("ReadAtUtc")]
    public void OptionalProperties_ShouldBeNullable(
        string propertyName)
    {
        var entity = BuildMappedEntity();

        var property = entity.FindProperty(propertyName);

        Assert.NotNull(property);
        Assert.True(property.IsNullable);
    }

    [Fact]
    public void IsRead_ShouldNotBeMapped()
    {
        var entity = BuildMappedEntity();

        Assert.Null(entity.FindProperty("IsRead"));
    }

    [Fact]
    public void ModuleScopedQueryIndex_ShouldExist()
    {
        var entity = BuildMappedEntity();

        var expectedColumns = new[]
        {
            "RecipientModuleProfileId",
            "Module",
            "CreatedAtUtc",
            "Id"
        };

        Assert.Contains(
            entity.GetIndexes(),
            index => index.Properties
                .Select(property => property.Name)
                .SequenceEqual(expectedColumns));
    }
}
