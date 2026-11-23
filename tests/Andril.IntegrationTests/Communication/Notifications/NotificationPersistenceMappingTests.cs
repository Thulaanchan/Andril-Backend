
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
    private const string ExpectedSchema =
        "andril_notifications";

    private const string ExpectedTable =
        "notifications";

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

        // Inspect mapping metadata only.
        // No database connection or migration execution.
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

    // D6-A5: Verify explicit schema and table mapping.
    [Fact]
    public void Notification_ShouldUseExpectedSchemaAndTable()
    {
        var entity = BuildMappedEntity();

        Assert.Equal(
            ExpectedSchema,
            entity.GetSchema());

        Assert.Equal(
            ExpectedTable,
            entity.GetTableName());
    }

    // Existing primary-key test with approved name verification.
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
            "pk_notifications",
            key.GetName());

        Assert.Equal(
            ValueGenerated.Never,
            entity.FindProperty("Id")!.ValueGenerated);
    }

    // Existing required-property tests.
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

    // Existing optional-property tests.
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

    // D6-A5: Exact PostgreSQL column names and types.
    [Theory]
    [InlineData("Id", "id", "uuid")]
    [InlineData(
        "RecipientModuleProfileId",
        "recipient_module_profile_id",
        "uuid")]
    [InlineData(
        "Module",
        "module",
        "character varying(16)")]
    [InlineData("Type", "type", "text")]
    [InlineData("Title", "title", "text")]
    [InlineData("Preview", "preview", "text")]
    [InlineData(
        "CreatedAtUtc",
        "created_at_utc",
        "timestamp with time zone")]
    [InlineData(
        "ReadAtUtc",
        "read_at_utc",
        "timestamp with time zone")]
    public void NotificationFields_ShouldUseExpectedColumnMapping(
        string propertyName,
        string expectedColumnName,
        string expectedColumnType)
    {
        var entity = BuildMappedEntity();

        var property = entity.FindProperty(propertyName);

        Assert.NotNull(property);

        var table = StoreObjectIdentifier.Table(
            ExpectedTable,
            ExpectedSchema);

        Assert.Equal(
            expectedColumnName,
            property.GetColumnName(table));

        Assert.Equal(
            expectedColumnType,
            property.GetColumnType());
    }

    // Existing computed-property exclusion.
    [Fact]
    public void IsRead_ShouldNotBeMapped()
    {
        var entity = BuildMappedEntity();

        Assert.Null(entity.FindProperty("IsRead"));
    }

    // Existing index test with approved index-name verification.
    [Fact]
    public void ModuleScopedQueryIndex_ShouldExist()
    {
        var entity = BuildMappedEntity();

        var expectedProperties = new[]
        {
            "RecipientModuleProfileId",
            "Module",
            "CreatedAtUtc",
            "Id"
        };

        // Only the existing approved index is expected.
        var index = Assert.Single(entity.GetIndexes());

        Assert.Equal(
            expectedProperties,
            index.Properties
                .Select(property => property.Name)
                .ToArray());

        Assert.Equal(
            "ix_notifications_profile_module_created_id",
            index.GetDatabaseName());
    }

    // D6-A5: Prevent unexpected persisted/shadow properties.
    [Fact]
    public void Notification_ShouldContainOnlyApprovedProperties()
    {
        var entity = BuildMappedEntity();

        var expectedProperties = new[]
        {
            "Id",
            "RecipientModuleProfileId",
            "Module",
            "Type",
            "Title",
            "Preview",
            "CreatedAtUtc",
            "ReadAtUtc"
        };

        var actualProperties = entity.GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedProperties
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            actualProperties);

        Assert.All(
            entity.GetProperties(),
            property =>
                Assert.False(property.IsShadowProperty()));
    }

    // D6-A5: No cross-schema FK is approved yet.
    [Fact]
    public void Notification_ShouldNotIntroduceForeignKeys()
    {
        var entity = BuildMappedEntity();

        Assert.Empty(entity.GetForeignKeys());
        Assert.Empty(entity.GetNavigations());
        Assert.Empty(entity.GetSkipNavigations());
    }

    // D6-A5: Keep the standalone metadata model isolated.
    [Fact]
    public void Mapping_ShouldNotRegisterAccountsEntities()
    {
        var entity = BuildMappedEntity();

        Assert.Single(entity.Model.GetEntityTypes());
    }
}
