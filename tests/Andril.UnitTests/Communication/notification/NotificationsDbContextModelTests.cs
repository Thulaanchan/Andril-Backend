
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System;
using System.Linq;
using Xunit;

namespace Andril.UnitTests.Communication.Notification;

public sealed class NotificationsDbContextModelTests
{
    private const string NotificationEntityName =
        "Andril.Persistence.Repositories.Communication." +
        "NotificationPersistenceRecord";

    private const string ExpectedSchema =
        "andril_notifications";

    private const string ExpectedTable =
        "notifications";

    private static NotificationsDbContext CreateContext()
    {
        // Offline EF metadata verification only.
        // No PostgreSQL connection is opened.
        var options =
            new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql(
                    "Host=localhost;" +
                    "Database=andril_notifications_model_test;" +
                    "Username=unused;" +
                    "Password=unused")
                .Options;

        return new NotificationsDbContext(options);
    }

    private static IEntityType GetNotificationEntity(
        NotificationsDbContext context)
    {
        return Assert.Single(
            context.Model.GetEntityTypes(),
            entity =>
                entity.ClrType.FullName == NotificationEntityName);
    }

    // D6-A4: Notification entity isolation.
    [Fact]
    public void Model_ShouldContainOnlyNotificationEntity()
    {
        using var context = CreateContext();

        var entity = Assert.Single(
            context.Model.GetEntityTypes());

        Assert.Equal(
            NotificationEntityName,
            entity.ClrType.FullName);
    }

    // D6-A4: Explicit schema and table mapping.
    [Fact]
    public void NotificationEntity_ShouldUseExpectedSchemaAndTable()
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        Assert.Equal(ExpectedSchema, entity.GetSchema());
        Assert.Equal(ExpectedTable, entity.GetTableName());
    }

    // Existing primary-key verification.
    [Fact]
    public void NotificationId_ShouldBePrimaryKey()
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        var primaryKey = Assert.IsAssignableFrom<IKey>(
            entity.FindPrimaryKey());

        Assert.Equal(
            new[] { "Id" },
            primaryKey.Properties
                .Select(property => property.Name)
                .ToArray());

        // D6-A4: Explicit primary-key constraint name.
        Assert.Equal(
            "pk_notifications",
            primaryKey.GetName());
    }

    [Fact]
    public void NotificationId_ShouldNotBeDatabaseGenerated()
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        var id = Assert.IsAssignableFrom<IProperty>(
            entity.FindProperty("Id"));

        Assert.Equal(
            ValueGenerated.Never,
            id.ValueGenerated);
    }

    // Existing required-field checks.
    [Theory]
    [InlineData("RecipientModuleProfileId")]
    [InlineData("Module")]
    [InlineData("Type")]
    [InlineData("Title")]
    [InlineData("CreatedAtUtc")]
    public void RequiredFields_ShouldBeNonNullable(
        string propertyName)
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        var property = Assert.IsAssignableFrom<IProperty>(
            entity.FindProperty(propertyName));

        Assert.False(property.IsNullable);
    }

    // Existing optional-field checks.
    [Theory]
    [InlineData("Preview")]
    [InlineData("ReadAtUtc")]
    public void OptionalFields_ShouldBeNullable(
        string propertyName)
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        var property = Assert.IsAssignableFrom<IProperty>(
            entity.FindProperty(propertyName));

        Assert.True(property.IsNullable);
    }

    // D6-A4: Exact PostgreSQL column names and types.
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
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        var property = Assert.IsAssignableFrom<IProperty>(
            entity.FindProperty(propertyName));

        var table = StoreObjectIdentifier.Table(
            ExpectedTable,
            ExpectedSchema);

        Assert.Equal(
            expectedColumnName,
            property.GetColumnName(table));

        Assert.Equal(
            expectedColumnType,
            property.GetColumnType(table));
    }

    // Existing composite index test, extended with exact name.
    [Fact]
    public void ModuleProfileCompositeIndex_ShouldExist()
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        string[] expectedProperties =
        {
            "RecipientModuleProfileId",
            "Module",
            "CreatedAtUtc",
            "Id"
        };

        // D6 scope permits only the existing index.
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

    // Existing computed-property exclusion.
    [Fact]
    public void IsRead_ShouldNotBeMappedToDatabase()
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        Assert.Null(entity.FindProperty("IsRead"));
    }

    // Existing field test, extended to prevent shadow columns.
    [Fact]
    public void NotificationFields_ShouldBeMapped()
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        string[] expectedProperties =
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

        foreach (var propertyName in expectedProperties)
        {
            Assert.NotNull(entity.FindProperty(propertyName));
        }

        // Reject unexpected persisted properties.
        var actualProperties = entity.GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedProperties
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            actualProperties);

        // Reject shadow columns.
        Assert.All(
            entity.GetProperties(),
            property => Assert.False(property.IsShadowProperty()));
    }

    // D6-A4: Composite FK is not approved in this slice.
    [Fact]
    public void NotificationEntity_ShouldNotIntroduceRelationships()
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        Assert.Empty(entity.GetForeignKeys());
        Assert.Empty(entity.GetNavigations());
        Assert.Empty(entity.GetSkipNavigations());

        // Ensure Accounts or other entities are not registered.
        Assert.Single(context.Model.GetEntityTypes());
    }
}
