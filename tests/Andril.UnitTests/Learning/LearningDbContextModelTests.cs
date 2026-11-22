using System.Data;
using System.Data.Common;
using System.Text.Json;
using Andril.Learning.Entities;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Andril.UnitTests.Learning;

/// <summary>Offline EF metadata and tracking only; no PostgreSQL materialization or authorization proof.</summary>
public sealed class LearningDbContextModelTests
{
    private static IEntityType Entity(OfflineContext fixture, bool selection) =>
        fixture.Context.GetService<IDesignTimeModel>().Model.FindEntityType(
            selection ? typeof(LearningSkillSelection) : typeof(LearningPersonalGoal))!;

    private static IProperty Property(IEntityType entity, string name) =>
        Assert.IsAssignableFrom<IProperty>(entity.FindProperty(name));

    [Fact]
    public void ModelInitializesWithOnlyTheTwoLearningEntities()
    {
        using var fixture = new OfflineContext();
        var model = fixture.Context.Model;
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", fixture.Context.Database.ProviderName);
        Assert.Equal("andril_learning", model.GetDefaultSchema());
        Assert.Equal(new[] { typeof(LearningPersonalGoal), typeof(LearningSkillSelection) },
            model.GetEntityTypes().Select(entity => entity.ClrType).OrderBy(type => type.Name).ToArray());
        Assert.Equal(2, model.GetRelationalModel().Tables.Count());
    }

    [Theory]
    [InlineData(true, "learning_skill_selections")]
    [InlineData(false, "learning_personal_goals")]
    public void TablesRemainInTheLearningSchema(bool selection, string table)
    {
        using var fixture = new OfflineContext();
        var entity = Entity(fixture, selection);
        Assert.Equal(table, entity.GetTableName());
        Assert.Equal("andril_learning", entity.GetSchema());
    }

    [Theory]
    [InlineData(true, "pk_learning_skill_selections")]
    [InlineData(false, "pk_learning_personal_goals")]
    public void PrimaryKeysPreserveApplicationAssignedGoalAndSelectionIdentities(bool selection, string name)
    {
        using var fixture = new OfflineContext();
        var entity = Entity(fixture, selection);
        var key = Assert.IsAssignableFrom<IKey>(entity.FindPrimaryKey());
        Assert.Equal("Id", Assert.Single(key.Properties).Name);
        Assert.Equal(name, key.GetName());
        Assert.Equal(ValueGenerated.Never, Property(entity, "Id").ValueGenerated);
        Assert.Single(entity.GetKeys());
    }

    [Theory]
    [InlineData(true, "Id", "id")]
    [InlineData(true, "LearningProfileId", "learning_profile_id")]
    [InlineData(true, "SkillId", "skill_id")]
    [InlineData(false, "Id", "id")]
    [InlineData(false, "LearningProfileId", "learning_profile_id")]
    public void IdentifiersAreRequiredUuidColumns(bool selection, string name, string column)
    {
        using var fixture = new OfflineContext();
        var entity = Entity(fixture, selection);
        var property = Property(entity, name);
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        Assert.Equal(column, property.GetColumnName(table));
        Assert.Equal(typeof(Guid), property.ClrType);
        Assert.Equal("uuid", property.GetColumnType());
        Assert.False(property.IsNullable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModuleColumnsMatchTheExistingProfileDiscriminatorRepresentation(bool selection)
    {
        using var fixture = new OfflineContext();
        var property = Property(Entity(fixture, selection), "Module");
        Assert.Equal("module", property.GetColumnName());
        Assert.Equal("character varying(16)", property.GetColumnType());
        Assert.Equal(16, property.GetMaxLength());
        Assert.False(property.IsNullable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyExistingRequiredPropertiesAreMappedThroughPrivateSetters(bool selection)
    {
        using var fixture = new OfflineContext();
        var entity = Entity(fixture, selection);
        var expected = selection
            ? new[] { "Id", "Kind", "LearningProfileId", "Module", "SkillId" }
            : new[] { "Id", "LearningProfileId", "Module", "Text" };
        Assert.Equal(expected, entity.GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.All(entity.GetProperties(), property =>
        {
            Assert.False(property.IsNullable);
            Assert.False(property.IsShadowProperty());
            Assert.Equal(PropertyAccessMode.Property, property.GetPropertyAccessMode());
            Assert.NotNull(property.PropertyInfo);
            Assert.True(property.PropertyInfo!.SetMethod!.IsPrivate);
        });
    }

    [Theory]
    [InlineData(LearningSkillSelectionKind.Teaching, 1)]
    [InlineData(LearningSkillSelectionKind.Learning, 2)]
    public void SkillPurposeConversionPreservesExistingEnumValues(LearningSkillSelectionKind kind, int stored)
    {
        using var fixture = new OfflineContext();
        var property = Property(Entity(fixture, true), "Kind");
        Assert.Equal("kind", property.GetColumnName());
        Assert.Equal("integer", property.GetColumnType());
        var converter = property.GetTypeMapping().Converter;
        Assert.NotNull(converter);
        Assert.Equal(typeof(int), converter!.ProviderClrType);
        Assert.Equal(stored, Assert.IsType<int>(converter.ConvertToProvider(kind)));
        Assert.Equal(kind, Assert.IsType<LearningSkillSelectionKind>(converter.ConvertFromProvider(stored)));
    }

    [Fact]
    public void GoalContentIsRequiredUnboundedTextWithoutLifecycleProperties()
    {
        using var fixture = new OfflineContext();
        var property = Property(Entity(fixture, false), "Text");
        Assert.Equal("text", property.GetColumnName());
        Assert.Equal("text", property.GetColumnType());
        Assert.Equal(typeof(string), property.ClrType);
        Assert.False(property.IsNullable);
        Assert.Null(property.GetMaxLength());
        Assert.Null(property.GetValueConverter());
    }

    [Fact]
    public void ExistingModuleAndKindConstraintsArePreservedWithoutInventingPolicies()
    {
        using var fixture = new OfflineContext();
        var selections = Entity(fixture, true).GetCheckConstraints().ToDictionary(check => check.Name, check => check.Sql);
        Assert.Equal(2, selections.Count);
        Assert.Equal("module = 'learning'", selections["ck_learning_skill_selection_module"]);
        Assert.Equal("kind IN (1, 2)", selections["ck_learning_skill_selection_kind"]);
        var goalConstraint = Assert.Single(Entity(fixture, false).GetCheckConstraints());
        Assert.Equal("ck_learning_personal_goal_module", goalConstraint.Name);
        Assert.Equal("module = 'learning'", goalConstraint.Sql);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoAccountsRelationshipsCascadesOrSpeculativeIndexesAreIntroduced(bool selection)
    {
        using var fixture = new OfflineContext();
        var entity = Entity(fixture, selection);
        Assert.Empty(entity.GetForeignKeys());
        Assert.Empty(entity.GetReferencingForeignKeys());
        Assert.Empty(entity.GetNavigations());
        Assert.Empty(entity.GetSkipNavigations());
        Assert.Empty(entity.GetIndexes());
        // Scalar profile IDs and Learning-only metadata are not authenticated ownership evidence.
    }

    [Theory]
    [InlineData(LearningSkillSelectionKind.Teaching)]
    [InlineData(LearningSkillSelectionKind.Learning)]
    public void ExistingSelectionsCanBeTrackedWithoutGeneratingIdsOrOpeningConnections(LearningSkillSelectionKind kind)
    {
        using var fixture = new OfflineContext();
        var preferences = new LearningPreferences(Guid.NewGuid(), Guid.NewGuid());
        var teaching = Guid.NewGuid(); var learning = Guid.NewGuid();
        preferences.UpdateSelections([], [teaching], [learning]);
        var id = Guid.NewGuid();
        var row = LearningSkillSelection.CreateFromPreferences(id, preferences,
            kind == LearningSkillSelectionKind.Teaching ? teaching : learning, kind);
        var entry = fixture.Context.Attach(row);
        Assert.Equal(EntityState.Unchanged, entry.State);
        Assert.False(entry.Property(selection => selection.Id).IsTemporary);
        Assert.Equal(id, row.Id);
        Assert.Equal(preferences.LearningProfileId, row.LearningProfileId);
        Assert.Equal("learning", row.Module);
        var replacement = Guid.NewGuid();
        entry.Property(selection => selection.SkillId).CurrentValue = replacement;
        fixture.Context.ChangeTracker.DetectChanges();
        Assert.Equal(replacement, row.SkillId);
        Assert.True(entry.Property(selection => selection.SkillId).IsModified);
        Assert.Equal(id, row.Id);
        Assert.Equal(teaching, Assert.Single(preferences.TeachingSkills));
        Assert.Equal(learning, Assert.Single(preferences.LearningSkills));
        // EF property access bypasses the factory; only a future authorized repository may use it.
    }

    [Fact]
    public void GoalTrackingUsesPrivateSetterWithoutNormalizingTextOrChangingOwnership()
    {
        using var fixture = new OfflineContext();
        var preferences = new LearningPreferences(Guid.NewGuid(), Guid.NewGuid());
        var goal = preferences.CreateGoal("  Private தமிழ் සිංහල e\u0301 👩🏽‍💻\n  ");
        var row = LearningPersonalGoal.CreateFromPreferences(preferences, goal.Id);
        var entry = fixture.Context.Attach(row);
        Assert.Equal(EntityState.Unchanged, entry.State);
        Assert.False(entry.Property(value => value.Id).IsTemporary);
        Assert.Equal(goal.Text, row.Text);
        const string replacement = "  Changed தமிழ் e\u0301\n  ";
        entry.Property(value => value.Text).CurrentValue = replacement;
        fixture.Context.ChangeTracker.DetectChanges();
        Assert.Equal(replacement, row.Text);
        Assert.True(entry.Property(value => value.Text).IsModified);
        Assert.Equal(goal.Id, row.Id);
        Assert.Equal(preferences.LearningProfileId, row.LearningProfileId);
        Assert.Equal("learning", row.Module);
        Assert.NotEqual(replacement, goal.Text);
    }

    [Fact]
    public void TrackingDoesNotMakePrivateRowsDefaultJsonPayloads()
    {
        using var fixture = new OfflineContext();
        var preferences = new LearningPreferences(Guid.NewGuid(), Guid.NewGuid());
        var skill = Guid.NewGuid();
        preferences.UpdateSelections([], [], [skill]);
        var selection = LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), preferences, skill,
            LearningSkillSelectionKind.Learning);
        var goal = preferences.CreateGoal("PRIVATE-EF-GOAL");
        var row = LearningPersonalGoal.CreateFromPreferences(preferences, goal.Id);
        fixture.Context.Attach(selection);
        fixture.Context.Attach(row);
        Assert.Equal("{}", JsonSerializer.Serialize(selection));
        Assert.Equal("{}", JsonSerializer.Serialize(row));
        Assert.Equal(goal.Text, row.Text);
        Assert.Equal(skill, selection.SkillId);
    }

    private sealed class OfflineContext : IDisposable
    {
        private readonly RejectConnections connections = new();
        internal LearningDbContext Context { get; }

        internal OfflineContext()
        {
            var options = new DbContextOptionsBuilder<LearningDbContext>()
                .UseNpgsql("Host=127.0.0.1;Database=andril_learning_model_only;Username=unused;Pooling=false")
                .AddInterceptors(connections).Options;
            Context = new LearningDbContext(options);
        }

        public void Dispose()
        {
            try
            {
                Assert.Equal(0, connections.OpenAttempts);
                Assert.Equal(ConnectionState.Closed, Context.Database.GetDbConnection().State);
            }
            finally { Context.Dispose(); }
        }
    }

    private sealed class RejectConnections : DbConnectionInterceptor
    {
        internal int OpenAttempts { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Database connections are forbidden in offline Learning model tests.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Database connections are forbidden in offline Learning model tests.");
        }
    }
}
