
using System;
using System.Linq;
using System.Reflection;
using Andril.Communication.Notifications;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Communication;
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

    private static readonly Guid ReconstructionProfileId =
        Guid.Parse("38000000-0000-0000-0000-000000000100");

    private static readonly DateTimeOffset ReconstructionCreatedAt =
        new(2026, 11, 28, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("learning", NotificationModule.Learning, false, "38000000-0000-0000-0000-000000000001")]
    [InlineData("learning", NotificationModule.Learning, true, "38000000-0000-0000-0000-000000000002")]
    [InlineData("hobbies", NotificationModule.Hobbies, false, "38000000-0000-0000-0000-000000000003")]
    [InlineData("hobbies", NotificationModule.Hobbies, true, "38000000-0000-0000-0000-000000000004")]
    [InlineData("social", NotificationModule.Social, false, "38000000-0000-0000-0000-000000000005")]
    [InlineData("social", NotificationModule.Social, true, "38000000-0000-0000-0000-000000000006")]
    public void ToDomain_ShouldPreservePrivateScopeFieldsAndReadState(
        string storedModule, NotificationModule expectedModule, bool isRead, string notificationId)
    {
        var convert = ResolveToDomain();
        var id = Guid.Parse(notificationId);
        var title = $"N38 {storedModule} {isRead} title";
        string? preview = isRead ? $"N38 {storedModule} read preview" : null;
        var expectedReadAt = ReconstructionCreatedAt.AddMinutes(15);
        var record = CreateReconstructionRecord(storedModule, id, title, preview, isRead);
        Assert.NotNull(record);
        var before = Snapshot(record);

        var notification = convert(record);

        Assert.NotNull(notification);
        Assert.Equal(id, notification.Id);
        Assert.Equal(expectedModule, notification.RecipientScope.Module);
        Assert.Equal(ReconstructionProfileId, notification.RecipientScope.RecipientModuleProfileId);
        Assert.Equal(new NotificationModuleScope(expectedModule, ReconstructionProfileId),
            notification.RecipientScope);
        Assert.Equal("message", notification.Type);
        Assert.Equal(title, notification.Title);
        Assert.Equal(preview, notification.Preview);
        Assert.Equal(ReconstructionCreatedAt, notification.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, notification.CreatedAtUtc.Offset);
        if (isRead)
        {
            Assert.Equal(expectedReadAt, record.ReadAtUtc);
            Assert.Equal(expectedReadAt, notification.ReadAtUtc);
            Assert.Equal(record.ReadAtUtc, notification.ReadAtUtc);
            Assert.Equal(TimeSpan.Zero, notification.ReadAtUtc!.Value.Offset);
            Assert.True(notification.IsRead);
        }
        else
        {
            Assert.Null(record.ReadAtUtc);
            Assert.Null(notification.ReadAtUtc);
            Assert.False(notification.IsRead);
        }

        Assert.Equal(before, Snapshot(record));
        AssertPairwiseReconstructedScopes(convert);
    }

    [Fact]
    public void ToDomain_UnsupportedModule_ShouldRejectWithoutPrivateDiagnosticsOrMutation()
    {
        // Resolve/validate reflection infrastructure outside the rejection assertion.
        var convert = ResolveToDomain();
        const string title = "PRIVATE_N38_TITLE_7F4D";
        const string preview = "PRIVATE_N38_PREVIEW_8A1C";
        const string type = "PRIVATE_N38_KIND_9B2E";
        var id = Guid.Parse("38000000-0000-0000-0000-000000000099");
        var record = NotificationPersistenceRecord.Create(id, ReconstructionProfileId,
            "unknown-module", type, title, preview, ReconstructionCreatedAt);
        var before = Snapshot(record);
        NotificationEntry? result = null;

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            result = convert(record);
        });

        // No returned object means no fallback identity in any module.
        Assert.Null(result);
        var diagnostic = exception.ToString();
        foreach (var sentinel in new[] { title, preview, type, id.ToString("D"),
                     ReconstructionProfileId.ToString("D") })
        {
            Assert.DoesNotContain(sentinel, diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(before, Snapshot(record));
    }

    private static Func<NotificationPersistenceRecord, NotificationEntry> ResolveToDomain()
    {
        var method = Assert.Single(typeof(PostgresNotificationRepository)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static),
            candidate => candidate.Name == "ToDomain");
        Assert.True(method.IsPrivate);
        Assert.True(method.IsStatic);
        Assert.Equal(typeof(NotificationEntry), method.ReturnType);
        Assert.Equal(typeof(NotificationPersistenceRecord), Assert.Single(method.GetParameters()).ParameterType);
        // A direct delegate invokes the real static converter without a
        // TargetInvocationException wrapper; production exceptions stay intact.
        return method.CreateDelegate<Func<NotificationPersistenceRecord, NotificationEntry>>();
    }

    private static NotificationPersistenceRecord CreateReconstructionRecord(
        string storedModule, Guid id, string title, string? preview, bool isRead)
    {
        var record = NotificationPersistenceRecord.Create(id, ReconstructionProfileId,
            storedModule, "message", title, preview, ReconstructionCreatedAt);
        if (isRead)
            record.MarkAsRead(ReconstructionCreatedAt.AddMinutes(15));
        return record;
    }

    private static (Guid Id, Guid Profile, string Module, string Type, string Title,
        string? Preview, DateTimeOffset Created, DateTimeOffset? Read, bool IsRead)
        Snapshot(NotificationPersistenceRecord record) =>
        (record.Id, record.RecipientModuleProfileId, record.Module, record.Type, record.Title,
            record.Preview, record.CreatedAtUtc, record.ReadAtUtc, record.IsRead);

    private static void AssertPairwiseReconstructedScopes(
        Func<NotificationPersistenceRecord, NotificationEntry> convert)
    {
        var learning = convert(CreateReconstructionRecord("learning",
            Guid.Parse("38000000-0000-0000-0000-000000000101"), "N38 Learning", null, false)).RecipientScope;
        var hobbies = convert(CreateReconstructionRecord("hobbies",
            Guid.Parse("38000000-0000-0000-0000-000000000102"), "N38 Hobbies", null, false)).RecipientScope;
        var social = convert(CreateReconstructionRecord("social",
            Guid.Parse("38000000-0000-0000-0000-000000000103"), "N38 Social", null, false)).RecipientScope;

        Assert.Equal(NotificationModule.Learning, learning.Module);
        Assert.Equal(NotificationModule.Hobbies, hobbies.Module);
        Assert.Equal(NotificationModule.Social, social.Module);
        Assert.Equal(ReconstructionProfileId, learning.RecipientModuleProfileId);
        Assert.Equal(ReconstructionProfileId, hobbies.RecipientModuleProfileId);
        Assert.Equal(ReconstructionProfileId, social.RecipientModuleProfileId);
        Assert.NotEqual(learning, hobbies);
        Assert.NotEqual(learning, social);
        Assert.NotEqual(hobbies, social);
    }

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
