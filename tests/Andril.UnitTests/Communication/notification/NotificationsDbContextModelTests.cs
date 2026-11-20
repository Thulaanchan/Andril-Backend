
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using Xunit;

namespace Andril.UnitTests.Communication.Notification;

public sealed class NotificationsDbContextModelTests
{
    private const string NotificationEntityName =
        "Andril.Persistence.Repositories.Communication." +
        "NotificationPersistenceRecord";

    private static NotificationsDbContext CreateContext()
    {
        // Model-only connection string.
        // No real PostgreSQL connection is opened.
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
        // The persistence record is internal.
        // Locate it through EF Core metadata instead
        // of changing its accessibility.
        return Assert.Single(
            context.Model.GetEntityTypes(),
            entity =>
                entity.ClrType.FullName == NotificationEntityName);
    }

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

        var indexExists = entity.GetIndexes().Any(
            index =>
                index.Properties
                    .Select(property => property.Name)
                    .SequenceEqual(expectedProperties));

        Assert.True(
            indexExists,
            "Expected module-profile-scoped notification index.");
    }

    [Fact]
    public void IsRead_ShouldNotBeMappedToDatabase()
    {
        using var context = CreateContext();

        var entity = GetNotificationEntity(context);

        Assert.Null(entity.FindProperty("IsRead"));
    }

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
            Assert.NotNull(
                entity.FindProperty(propertyName));
        }
    }
}
