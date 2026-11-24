using System.Data;
using System.Data.Common;
using Andril.Persistence.DbContexts;
using Andril.Social.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SocialDbContextMappingTests
{
    private const string OfflineConnectionString = "Host=localhost;Database=not_connected;Username=not_connected";

    [Fact]
    public void SocialDbContext_ModelHasApprovedSchemaAndTables()
    {
        var guard = new RejectConnections();
        using var context = new SocialDbContext(new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(OfflineConnectionString).AddInterceptors(guard).Options);

        var model = context.GetService<IDesignTimeModel>().Model;

        var responseEntity = model.FindEntityType(typeof(QuestionnaireResponseEntity))!;
        Assert.Equal(SocialDbContext.Schema, responseEntity.GetSchema());
        Assert.Equal("social_questionnaire_responses", responseEntity.GetTableName());

        var answerEntity = model.FindEntityType(typeof(QuestionnaireAnswerEntity))!;
        Assert.Equal(SocialDbContext.Schema, answerEntity.GetSchema());
        Assert.Equal("social_questionnaire_answers", answerEntity.GetTableName());

        Assert.Equal(0, guard.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void SocialDbContext_PrimaryKeysAndForeignKeys_AreConfiguredCorrectly()
    {
        var guard = new RejectConnections();
        using var context = new SocialDbContext(new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(OfflineConnectionString).AddInterceptors(guard).Options);

        var model = context.GetService<IDesignTimeModel>().Model;

        var responseEntity = model.FindEntityType(typeof(QuestionnaireResponseEntity))!;
        var responsePk = responseEntity.FindPrimaryKey()!;
        Assert.Equal(nameof(QuestionnaireResponseEntity.Id), Assert.Single(responsePk.Properties).Name);
        Assert.Equal("pk_social_questionnaire_responses", responsePk.GetName());

        var answerEntity = model.FindEntityType(typeof(QuestionnaireAnswerEntity))!;
        var answerPk = answerEntity.FindPrimaryKey()!;
        Assert.Equal(nameof(QuestionnaireAnswerEntity.Id), Assert.Single(answerPk.Properties).Name);
        Assert.Equal("pk_social_questionnaire_answers", answerPk.GetName());

        var fk = Assert.Single(answerEntity.GetForeignKeys());
        Assert.Equal(typeof(QuestionnaireResponseEntity), fk.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(QuestionnaireAnswerEntity.ResponseId), Assert.Single(fk.Properties).Name);
        Assert.Equal(nameof(QuestionnaireResponseEntity.Id), Assert.Single(fk.PrincipalKey.Properties).Name);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
        Assert.Equal("fk_social_questionnaire_answers_response", fk.GetConstraintName());

        Assert.Equal(0, guard.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void SocialDbContext_IndexesAndConcurrencyToken_AreConfiguredCorrectly()
    {
        var guard = new RejectConnections();
        using var context = new SocialDbContext(new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(OfflineConnectionString).AddInterceptors(guard).Options);

        var model = context.GetService<IDesignTimeModel>().Model;

        var responseEntity = model.FindEntityType(typeof(QuestionnaireResponseEntity))!;
        var profileVersionIndex = Assert.Single(responseEntity.GetIndexes());
        Assert.Equal("ix_social_questionnaire_profile_version", profileVersionIndex.GetDatabaseName());
        Assert.Equal(new[] { nameof(QuestionnaireResponseEntity.ProfileId), nameof(QuestionnaireResponseEntity.Version) },
            profileVersionIndex.Properties.Select(p => p.Name));

        var rowVersion = responseEntity.FindProperty("RowVersion")!;
        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal("row_version", rowVersion.GetColumnName());

        var answerEntity = model.FindEntityType(typeof(QuestionnaireAnswerEntity))!;
        var answerIndex = Assert.Single(answerEntity.GetIndexes());
        Assert.Equal("ix_social_questionnaire_answers_response", answerIndex.GetDatabaseName());
        Assert.Equal(nameof(QuestionnaireAnswerEntity.ResponseId), Assert.Single(answerIndex.Properties).Name);

        Assert.Equal(0, guard.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void SocialDbContext_CheckConstraints_AreEnforcedInModel()
    {
        var guard = new RejectConnections();
        using var context = new SocialDbContext(new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(OfflineConnectionString).AddInterceptors(guard).Options);

        var model = context.GetService<IDesignTimeModel>().Model;

        var responseEntity = model.FindEntityType(typeof(QuestionnaireResponseEntity))!;
        var responseConstraints = responseEntity.GetCheckConstraints().Select(c => c.Name).ToArray();
        Assert.Contains("ck_questionnaire_response_id", responseConstraints);
        Assert.Contains("ck_questionnaire_profile_id", responseConstraints);
        Assert.Contains("ck_questionnaire_timestamp", responseConstraints);

        var answerEntity = model.FindEntityType(typeof(QuestionnaireAnswerEntity))!;
        var answerConstraints = answerEntity.GetCheckConstraints().Select(c => c.Name).ToArray();
        Assert.Contains("ck_questionnaire_answer_id", answerConstraints);
        Assert.Contains("ck_questionnaire_answer_response_id", answerConstraints);
        Assert.Contains("ck_questionnaire_answer_timestamp", answerConstraints);

        Assert.Equal(0, guard.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void SocialDbContext_GenerateCreateScript_TargetsOnlySocialSchema()
    {
        var guard = new RejectConnections();
        using var context = new SocialDbContext(new DbContextOptionsBuilder<SocialDbContext>()
            .UseNpgsql(OfflineConnectionString).AddInterceptors(guard).Options);

        var sql = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE andril_social.social_questionnaire_responses", sql);
        Assert.Contains("CREATE TABLE andril_social.social_questionnaire_answers", sql);
        Assert.Contains("CONSTRAINT pk_social_questionnaire_responses PRIMARY KEY (id)", sql);
        Assert.Contains("CONSTRAINT pk_social_questionnaire_answers PRIMARY KEY (id)", sql);
        Assert.Contains("CONSTRAINT fk_social_questionnaire_answers_response FOREIGN KEY (response_id) REFERENCES andril_social.social_questionnaire_responses (id)", sql);
        Assert.Contains("ix_social_questionnaire_profile_version", sql);
        Assert.Contains("ix_social_questionnaire_answers_response", sql);
        Assert.Contains("ck_questionnaire_response_id", sql);
        Assert.Contains("ck_questionnaire_answer_id", sql);

        Assert.DoesNotContain("andril_identity", sql);
        Assert.DoesNotContain("andril_hobbies", sql);
        Assert.DoesNotContain("DROP TABLE", sql);

        Assert.Equal(0, guard.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private sealed class RejectConnections : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("The offline model must never open a database connection.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("The offline model must never open a database connection.");
        }
    }
}
