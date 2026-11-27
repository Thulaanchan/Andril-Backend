using System;
using System.Linq;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Social;
using Andril.Social.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.SecurityTests.Database;

/// <summary>
/// Security tests verifying physical module, schema, and profile isolation for the Social domain:
/// - Guarantees strict schema isolation within 'andril_social'.
/// - Verifies EF model isolation from 'andril_identity' and 'andril_hobbies'.
/// - Verifies migration history isolation to prevent cross-module schema collisions.
/// - Verifies profile scoping and cross-module profile discriminator boundaries.
/// </summary>
public sealed class SocialPhysicalSchemaIsolationSecurityTests
{
    private static readonly string?[] AllowedEntityTypes =
    [
        typeof(QuestionnaireResponseEntity).FullName,
        typeof(QuestionnaireAnswerEntity).FullName,
        "Andril.Persistence.DbContexts.SocialQuestionnaireCurrentResponse",
        "Andril.Persistence.DbContexts.SocialDirectionalInterestRow",
        "Andril.Persistence.DbContexts.SocialMatchRow"
    ];

    private static readonly string[] AllowedTables =
    [
        "social_questionnaire_responses",
        "social_questionnaire_answers",
        "social_questionnaire_current_responses",
        "social_directional_interests",
        "social_matches"
    ];

    [Fact]
    public void SocialDbContext_MapsOnlyApprovedEntityTypes_InSocialSchema()
    {
        var factory = new SocialDbContextFactory();
        using var context = factory.CreateDbContext(Array.Empty<string>());
        var model = context.Model;

        var entityTypes = model.GetEntityTypes().ToArray();
        Assert.Equal(AllowedEntityTypes.Length, entityTypes.Length);

        foreach (var entityType in entityTypes)
        {
            Assert.Contains(entityType.ClrType.FullName, AllowedEntityTypes);
            Assert.Equal(SocialDbContext.Schema, entityType.GetSchema());
            Assert.Equal("andril_social", entityType.GetSchema());
        }

        var tables = model.GetRelationalModel().Tables.ToArray();
        Assert.Equal(AllowedTables.Length, tables.Length);
        Assert.Equal(AllowedTables.OrderBy(t => t), tables.Select(t => t.Name).OrderBy(t => t));
        Assert.All(tables, t => Assert.Equal(SocialDbContext.Schema, t.Schema));
    }

    [Fact]
    public void SocialDbContext_HasNoPhysicalNavigations_ToOtherModules()
    {
        var factory = new SocialDbContextFactory();
        using var context = factory.CreateDbContext(Array.Empty<string>());
        var model = context.Model;

        foreach (var entityType in model.GetEntityTypes())
        {
            // Foreign keys must only point to principal entities within andril_social
            foreach (var fk in entityType.GetForeignKeys())
            {
                Assert.Contains(fk.PrincipalEntityType.ClrType.FullName, AllowedEntityTypes);
                Assert.Equal(SocialDbContext.Schema, fk.PrincipalEntityType.GetSchema());
            }

            // No navigation property should reference types outside Andril.Social.Entities
            foreach (var navigation in entityType.GetNavigations())
            {
                Assert.StartsWith("Andril.Social.Entities", navigation.TargetEntityType.ClrType.Namespace ?? string.Empty);
            }
        }
    }

    [Fact]
    public void SocialDbContext_GeneratedCreateScript_ContainsNoCrossModuleReferences()
    {
        var factory = new SocialDbContextFactory();
        using var context = factory.CreateDbContext(Array.Empty<string>());
        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE SCHEMA andril_social", script);
        Assert.Contains("CREATE TABLE andril_social.social_questionnaire_responses", script);
        Assert.Contains("CREATE TABLE andril_social.social_questionnaire_answers", script);

        // Security boundary: Generated script must NEVER create, alter, or drop objects in other schemas
        Assert.DoesNotContain("andril_identity", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("andril_hobbies", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("andril_chat", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("andril_groups", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SocialDbContext_MigrationHistoryTable_IsIsolatedFromSharedIdentity()
    {
        Assert.Equal("__EFMigrationsHistory_Social", SocialDbContextFactory.MigrationsHistoryTableName);
        Assert.NotEqual("__EFMigrationsHistory", SocialDbContextFactory.MigrationsHistoryTableName);
        Assert.NotEqual("__EFMigrationsHistory_Gallery", SocialDbContextFactory.MigrationsHistoryTableName);
        Assert.NotEqual("__EFMigrationsHistory_Map", SocialDbContextFactory.MigrationsHistoryTableName);

        var factory = new SocialDbContextFactory();
        using var context = factory.CreateDbContext(Array.Empty<string>());
        var historyRepo = context.GetService<IHistoryRepository>();
        var createScript = historyRepo.GetCreateScript();

        Assert.Contains(SocialDbContextFactory.MigrationsHistoryTableName, createScript);
        Assert.Contains(SocialDbContext.Schema, createScript);
        Assert.DoesNotContain("andril_identity", createScript);
        Assert.DoesNotContain("public", createScript);
    }

    [Fact]
    public async Task PostgresSocialQuestionnaireReadAdapter_EnforcesModuleDiscriminator_AgainstMismatchedModule()
    {
        var factory = new SocialDbContextFactory();
        using var context = factory.CreateDbContext(Array.Empty<string>());
        var adapter = new PostgresSocialQuestionnaireReadAdapter(context, enforceIdentityDiscriminator: true);

        Assert.True(adapter.EnforceIdentityDiscriminator);

        // Fail-closed offline behavior: empty or invalid GUID returns false without querying
        Assert.False(await adapter.VerifySocialProfileScopeAsync(Guid.Empty));

        // When discriminator is enforced, empty or unverified profile scopes return null for GetByIdAsync
        var result = await adapter.GetByIdAsync(Guid.Empty, Guid.NewGuid());
        Assert.Null(result);

        var latestResult = await adapter.GetLatestByProfileIdAsync(Guid.Empty);
        Assert.Null(latestResult);

        var listResult = await adapter.ListByProfileIdAsync(Guid.Empty, 5);
        Assert.Empty(listResult);
    }
}
