using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Social.Entities;
using Andril.Social.Personality;
using Andril.Social.Validators;
using Xunit;

namespace Andril.UnitTests.Social;

/// <summary>
/// Comprehensive unit, immutability, and privacy regression tests for <see cref="QuestionnaireResponseEntity"/>.
/// </summary>
public sealed class QuestionnaireResponseEntityTests
{
    private static readonly Guid SyntheticResponseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SyntheticProfileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset SyntheticTimestampUtc = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static QuestionnaireDefinition ProductionV1Definition => QuestionnaireV1Definition.Create();

    private static QuestionnaireAnswerEntity CreateAnswer(
        string questionId,
        string optionId = "1",
        Guid? responseId = null,
        Guid? answerId = null) =>
        new(
            answerId ?? Guid.NewGuid(),
            responseId ?? SyntheticResponseId,
            questionId,
            optionId,
            SyntheticTimestampUtc);

    private static List<QuestionnaireAnswerEntity> CreateAllTwelveV1Answers(Guid? responseId = null) =>
        Enumerable.Range(1, 12)
            .Select(i => CreateAnswer($"q{i:D2}", "3", responseId: responseId))
            .ToList();

    #region A. Successful factory construction

    [Fact]
    public void Create_EmptyAnswers_WithoutSkip_YieldsNotStartedStatus()
    {
        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            Array.Empty<QuestionnaireAnswerEntity>(),
            skip: false,
            SyntheticTimestampUtc);

        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, entity.Status);
        Assert.Empty(entity.Answers);
    }

    [Fact]
    public void Create_PartialAnswers_YieldsInProgressStatus()
    {
        var answers = new[]
        {
            CreateAnswer("q01", "2"),
            CreateAnswer("q02", "4")
        };

        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answers,
            skip: false,
            SyntheticTimestampUtc);

        Assert.Equal(QuestionnaireParticipationStatus.InProgress, entity.Status);
        Assert.Equal(2, entity.Answers.Count);
    }

    [Fact]
    public void Create_AllTwelveRequiredV1Answers_YieldsCompletedStatus()
    {
        var answers = CreateAllTwelveV1Answers();

        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answers,
            skip: false,
            SyntheticTimestampUtc);

        Assert.Equal(QuestionnaireParticipationStatus.Completed, entity.Status);
        Assert.Equal(12, entity.Answers.Count);
    }

    [Fact]
    public void Create_EmptyAnswers_WithSkipTrue_YieldsSkippedStatus()
    {
        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            Array.Empty<QuestionnaireAnswerEntity>(),
            skip: true,
            SyntheticTimestampUtc);

        Assert.Equal(QuestionnaireParticipationStatus.Skipped, entity.Status);
        Assert.Empty(entity.Answers);
    }

    [Fact]
    public void Create_PreservesCallerSuppliedIdentifiersAndTimestampsExactly()
    {
        var customResponseId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var customProfileId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var customTimestamp = new DateTimeOffset(2026, 5, 10, 8, 30, 0, TimeSpan.Zero);
        var answers = new[] { CreateAnswer("q01", "1", responseId: customResponseId) };

        var entity = QuestionnaireResponseEntity.Create(
            customResponseId,
            customProfileId,
            ProductionV1Definition,
            answers,
            skip: false,
            customTimestamp);

        Assert.Equal(customResponseId, entity.Id);
        Assert.Equal(customProfileId, entity.ProfileId);
        Assert.Equal("v1", entity.Version);
        Assert.Equal(customTimestamp, entity.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, entity.CreatedAtUtc.Offset);
        Assert.Single(entity.Answers);
        Assert.Equal("q01", entity.Answers[0].QuestionId);
        Assert.Equal("1", entity.Answers[0].OptionId);
    }

    [Fact]
    public void Create_DoesNotGenerateReplacementIdentitiesOrTimestamps()
    {
        var expectedResponseId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var expectedProfileId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var expectedTimestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var entity = QuestionnaireResponseEntity.Create(
            expectedResponseId,
            expectedProfileId,
            ProductionV1Definition,
            Array.Empty<QuestionnaireAnswerEntity>(),
            skip: false,
            expectedTimestamp);

        Assert.Equal(expectedResponseId, entity.Id);
        Assert.Equal(expectedProfileId, entity.ProfileId);
        Assert.Equal(expectedTimestamp, entity.CreatedAtUtc);
    }

    #endregion

    #region B. Invalid factory arguments

    [Fact]
    public void Create_EmptyResponseId_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                Guid.Empty,
                SyntheticProfileId,
                ProductionV1Definition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("id", ex.ParamName);
    }

    [Fact]
    public void Create_EmptyProfileId_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                Guid.Empty,
                ProductionV1Definition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("profileId", ex.ParamName);
    }

    [Fact]
    public void Create_NullDefinition_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                null!,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("definition", ex.ParamName);
    }

    [Fact]
    public void Create_NullAnswers_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                null!,
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("answers", ex.ParamName);
    }

    [Fact]
    public void Create_InvalidDefinition_ThrowsArgumentException()
    {
        var invalidDefinition = new QuestionnaireDefinition("v1", Array.Empty<QuestionDefinition>());

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                invalidDefinition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("definition", ex.ParamName);
    }

    #endregion

    #region C. Parent/child identity consistency

    [Fact]
    public void Create_AnswerWithForeignResponseId_ThrowsArgumentException()
    {
        var foreignResponseId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var answers = new[] { CreateAnswer("q01", "1", responseId: foreignResponseId) };

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                answers,
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("answers", ex.ParamName);
    }

    [Fact]
    public void Create_DuplicateAnswerEntityId_ThrowsArgumentException()
    {
        var sharedAnswerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var answers = new[]
        {
            CreateAnswer("q01", "1", answerId: sharedAnswerId),
            CreateAnswer("q02", "2", answerId: sharedAnswerId)
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                answers,
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("answers", ex.ParamName);
    }

    [Fact]
    public void Create_NullAnswerElement_ThrowsArgumentException()
    {
        var answers = new QuestionnaireAnswerEntity[] { null! };

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                answers,
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("answers", ex.ParamName);
    }

    [Fact]
    public void Create_DistinctValidAnswerIds_AreAcceptedAndPreserved()
    {
        var id1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
        var id2 = Guid.Parse("22222222-0000-0000-0000-000000000002");
        var answers = new[]
        {
            CreateAnswer("q01", "1", answerId: id1),
            CreateAnswer("q02", "2", answerId: id2)
        };

        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answers,
            skip: false,
            SyntheticTimestampUtc);

        Assert.Equal(id1, entity.Answers[0].Id);
        Assert.Equal(id2, entity.Answers[1].Id);
    }

    #endregion

    #region D. Questionnaire semantic validation

    [Fact]
    public void Create_UnknownQuestionId_ThrowsArgumentException()
    {
        var answers = new[] { CreateAnswer("q99_unknown", "1") };

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                answers,
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("answers", ex.ParamName);
    }

    [Fact]
    public void Create_UnknownOptionId_ThrowsArgumentException()
    {
        var answers = new[] { CreateAnswer("q01", "99_unknown_option") };

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                answers,
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("answers", ex.ParamName);
    }

    [Fact]
    public void Create_DuplicateQuestionAnswer_ThrowsArgumentException()
    {
        var answers = new[]
        {
            CreateAnswer("q01", "1"),
            CreateAnswer("q01", "2") // duplicate question answer
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                answers,
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("answers", ex.ParamName);
    }

    [Fact]
    public void Create_SkipTrueWithAnswers_ThrowsArgumentException()
    {
        var answers = new[] { CreateAnswer("q01", "1") };

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                answers,
                skip: true,
                SyntheticTimestampUtc));

        Assert.Equal("skip", ex.ParamName);
    }

    [Fact]
    public void Create_InvalidV1QuestionCount_ThrowsArgumentException()
    {
        var questions = Enumerable.Range(1, 11)
            .Select(i => new QuestionDefinition(
                $"q{i:D2}",
                $"Prompt {i}",
                IsRequired: true,
                new[] { new QuestionOption("1", "Opt") }))
            .ToArray();
        var invalidV1Definition = new QuestionnaireDefinition("v1", questions);

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                invalidV1Definition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("definition", ex.ParamName);
    }

    [Fact]
    public void Create_InvalidV1OptionalQuestion_ThrowsArgumentException()
    {
        var questions = Enumerable.Range(1, 12)
            .Select(i => new QuestionDefinition(
                $"q{i:D2}",
                $"Prompt {i}",
                IsRequired: i != 12, // V1 requires all 12 questions to be required
                new[] { new QuestionOption("1", "Opt") }))
            .ToArray();
        var invalidV1Definition = new QuestionnaireDefinition("v1", questions);

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                invalidV1Definition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                SyntheticTimestampUtc));

        Assert.Equal("definition", ex.ParamName);
    }

    #endregion

    #region E. UTC timestamp validation

    [Fact]
    public void Create_DefaultTimestamp_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                default));

        Assert.Equal("createdAtUtc", ex.ParamName);
    }

    [Fact]
    public void Create_MinValueTimestamp_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                DateTimeOffset.MinValue));

        Assert.Equal("createdAtUtc", ex.ParamName);
    }

    [Fact]
    public void Create_MaxValueTimestamp_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                DateTimeOffset.MaxValue));

        Assert.Equal("createdAtUtc", ex.ParamName);
    }

    [Theory]
    [InlineData(60)]    // +01:00
    [InlineData(330)]   // +05:30
    [InlineData(720)]   // +12:00
    public void Create_NonZeroPositiveOffset_ThrowsArgumentException(int offsetMinutes)
    {
        var nonUtc = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.FromMinutes(offsetMinutes));

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                nonUtc));

        Assert.Equal("createdAtUtc", ex.ParamName);
    }

    [Theory]
    [InlineData(-60)]   // -01:00
    [InlineData(-300)]  // -05:00
    [InlineData(-480)]  // -08:00
    public void Create_NonZeroNegativeOffset_ThrowsArgumentException(int offsetMinutes)
    {
        var nonUtc = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.FromMinutes(offsetMinutes));

        var ex = Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                Array.Empty<QuestionnaireAnswerEntity>(),
                skip: false,
                nonUtc));

        Assert.Equal("createdAtUtc", ex.ParamName);
    }

    [Fact]
    public void Create_ExplicitUtcZeroOffset_AcceptedAndPreserved()
    {
        var utcTimestamp = new DateTimeOffset(2026, 11, 22, 15, 45, 30, TimeSpan.Zero);

        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            Array.Empty<QuestionnaireAnswerEntity>(),
            skip: false,
            utcTimestamp);

        Assert.Equal(TimeSpan.Zero, entity.CreatedAtUtc.Offset);
        Assert.Equal(utcTimestamp, entity.CreatedAtUtc);
    }

    #endregion

    #region F. Defensive answer collection

    [Fact]
    public void Create_ModifyingCallerSourceListAfterCreation_DoesNotMutateEntityAnswers()
    {
        var answerList = new List<QuestionnaireAnswerEntity> { CreateAnswer("q01", "1") };

        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answerList,
            skip: false,
            SyntheticTimestampUtc);

        answerList.Add(CreateAnswer("q02", "2"));
        answerList.Clear();

        Assert.Single(entity.Answers);
        Assert.Equal("q01", entity.Answers[0].QuestionId);
    }

    [Fact]
    public void Create_ReplacingItemInOriginalInputArray_DoesNotMutateStoredSnapshot()
    {
        var originalAnswer = CreateAnswer("q01", "1");
        var answersArray = new[] { originalAnswer };

        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answersArray,
            skip: false,
            SyntheticTimestampUtc);

        answersArray[0] = CreateAnswer("q02", "2");

        Assert.Single(entity.Answers);
        Assert.Equal("q01", entity.Answers[0].QuestionId);
    }

    [Fact]
    public void ExposedAnswers_RejectsMutationWhenCastToIList()
    {
        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            new[] { CreateAnswer("q01", "1") },
            skip: false,
            SyntheticTimestampUtc);

        var list = entity.Answers as IList<QuestionnaireAnswerEntity>;
        Assert.NotNull(list);
        Assert.Throws<NotSupportedException>(() => list.Add(CreateAnswer("q02", "2")));
        Assert.Throws<NotSupportedException>(() => list.Remove(entity.Answers[0]));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list[0] = CreateAnswer("q02", "2"));
    }

    [Fact]
    public void ExposedAnswers_RepeatedReadsPreserveOrderAndValues()
    {
        var answers = new[]
        {
            CreateAnswer("q01", "1"),
            CreateAnswer("q02", "2"),
            CreateAnswer("q03", "3")
        };

        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answers,
            skip: false,
            SyntheticTimestampUtc);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(3, entity.Answers.Count);
            Assert.Equal("q01", entity.Answers[0].QuestionId);
            Assert.Equal("q02", entity.Answers[1].QuestionId);
            Assert.Equal("q03", entity.Answers[2].QuestionId);
        }
    }

    [Fact]
    public void Properties_DoNotExposePublicSetters()
    {
        var properties = typeof(QuestionnaireResponseEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(6, properties.Length);

        foreach (var property in properties)
        {
            var publicSetter = property.GetSetMethod(nonPublic: false);
            Assert.Null(publicSetter);
        }
    }

    [Fact]
    public void Entity_DoesNotExposeUnexpectedPublicMutationMethods()
    {
        var declaredMethods = typeof(QuestionnaireResponseEntity)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // filter out property getters
            .Select(m => m.Name)
            .ToList();

        // Only ToString override should be declared on QuestionnaireResponseEntity
        var unexpectedMethods = declaredMethods
            .Where(name => name != nameof(ToString))
            .ToList();

        Assert.Empty(unexpectedMethods);
    }

    #endregion

    #region G. Privacy and JSON serialization

    [Fact]
    public void Properties_AllHaveJsonIgnoreAttribute()
    {
        var properties = typeof(QuestionnaireResponseEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(6, properties.Length);

        foreach (var property in properties)
        {
            var jsonIgnore = property.GetCustomAttribute<JsonIgnoreAttribute>();
            Assert.NotNull(jsonIgnore);
        }
    }

    [Fact]
    public void JsonSerialization_DefaultOptions_ProducesEmptyJsonObject()
    {
        var answers = CreateAllTwelveV1Answers();
        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answers,
            skip: false,
            SyntheticTimestampUtc);

        var json = JsonSerializer.Serialize(entity);

        Assert.Equal("{}", json);
        Assert.DoesNotContain(SyntheticResponseId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticProfileId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Completed", json, StringComparison.Ordinal);
        Assert.DoesNotContain("q01", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Answers", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonSerialization_WithCustomNamingPolicy_DoesNotExposeConfidentialData()
    {
        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            new[] { CreateAnswer("q01", "1") },
            skip: false,
            SyntheticTimestampUtc);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(entity, options);

        Assert.DoesNotContain(SyntheticResponseId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticProfileId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("q01", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ReturnsSafeConstantDiagnosticString()
    {
        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            CreateAllTwelveV1Answers(),
            skip: false,
            SyntheticTimestampUtc);

        var result = entity.ToString();

        Assert.Equal("QuestionnaireResponseEntity [private]", result);
        Assert.DoesNotContain(SyntheticResponseId.ToString(), result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticProfileId.ToString(), result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v1", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Completed", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_MultipleDistinctInstances_ProduceIdenticalSafeRepresentation()
    {
        var entity1 = QuestionnaireResponseEntity.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ProductionV1Definition,
            Array.Empty<QuestionnaireAnswerEntity>(),
            skip: false,
            SyntheticTimestampUtc);

        var id2 = Guid.NewGuid();
        var entity2 = QuestionnaireResponseEntity.Create(
            id2,
            Guid.NewGuid(),
            ProductionV1Definition,
            CreateAllTwelveV1Answers(responseId: id2),
            skip: false,
            SyntheticTimestampUtc);

        Assert.Equal(entity1.ToString(), entity2.ToString());
        Assert.Equal("QuestionnaireResponseEntity [private]", entity1.ToString());
    }

    #endregion

    #region H. Optional participation and module isolation

    [Fact]
    public void Status_SkippedAndNotStarted_AreDistinctStatuses()
    {
        var notStartedEntity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            Array.Empty<QuestionnaireAnswerEntity>(),
            skip: false,
            SyntheticTimestampUtc);

        var skippedEntity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            Array.Empty<QuestionnaireAnswerEntity>(),
            skip: true,
            SyntheticTimestampUtc);

        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, notStartedEntity.Status);
        Assert.Equal(QuestionnaireParticipationStatus.Skipped, skippedEntity.Status);
        Assert.NotEqual(notStartedEntity.Status, skippedEntity.Status);
    }

    [Fact]
    public void Status_PartialSubmission_IsNeverMarkedCompleted()
    {
        // 11 of 12 required answers provided
        var answers = Enumerable.Range(1, 11)
            .Select(i => CreateAnswer($"q{i:D2}", "3"))
            .ToList();

        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answers,
            skip: false,
            SyntheticTimestampUtc);

        Assert.Equal(QuestionnaireParticipationStatus.InProgress, entity.Status);
        Assert.NotEqual(QuestionnaireParticipationStatus.Completed, entity.Status);
    }

    [Fact]
    public void ModuleIsolation_EntityOnlyExposesSocialProfileIdentity()
    {
        var properties = typeof(QuestionnaireResponseEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        Assert.Contains(nameof(QuestionnaireResponseEntity.ProfileId), properties);
        Assert.DoesNotContain("AccountId", properties);
        Assert.DoesNotContain("LearnerId", properties);
        Assert.DoesNotContain("HobbiesId", properties);
        Assert.DoesNotContain("Score", properties);
        Assert.DoesNotContain("MatchingWeight", properties);
        Assert.DoesNotContain("ExperiencePoints", properties);
    }

    #endregion

    #region I. Stable behavior

    [Fact]
    public void Create_RepeatedWithIdenticalInputs_ProducesIdenticalObservableProperties()
    {
        var answers1 = new[] { CreateAnswer("q01", "1"), CreateAnswer("q02", "2") };
        var answers2 = new[] { answers1[0], answers1[1] };

        var entity1 = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answers1,
            skip: false,
            SyntheticTimestampUtc);

        var entity2 = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answers2,
            skip: false,
            SyntheticTimestampUtc);

        Assert.Equal(entity1.Id, entity2.Id);
        Assert.Equal(entity1.ProfileId, entity2.ProfileId);
        Assert.Equal(entity1.Version, entity2.Version);
        Assert.Equal(entity1.Status, entity2.Status);
        Assert.Equal(entity1.CreatedAtUtc, entity2.CreatedAtUtc);
        Assert.Equal(entity1.Answers.Count, entity2.Answers.Count);
    }

    [Fact]
    public void Create_PreservesAnswerInputOrder()
    {
        var answers = new[]
        {
            CreateAnswer("q03", "1"),
            CreateAnswer("q01", "2"),
            CreateAnswer("q02", "3")
        };

        var entity = QuestionnaireResponseEntity.Create(
            SyntheticResponseId,
            SyntheticProfileId,
            ProductionV1Definition,
            answers,
            skip: false,
            SyntheticTimestampUtc);

        Assert.Equal("q03", entity.Answers[0].QuestionId);
        Assert.Equal("q01", entity.Answers[1].QuestionId);
        Assert.Equal("q02", entity.Answers[2].QuestionId);
    }

    [Fact]
    public void Create_FailedValidation_DoesNotMutateCallerCollection()
    {
        var invalidAnswerList = new List<QuestionnaireAnswerEntity>
        {
            CreateAnswer("q99_unknown", "1")
        };

        Assert.Throws<ArgumentException>(() =>
            QuestionnaireResponseEntity.Create(
                SyntheticResponseId,
                SyntheticProfileId,
                ProductionV1Definition,
                invalidAnswerList,
                skip: false,
                SyntheticTimestampUtc));

        Assert.Single(invalidAnswerList);
        Assert.Equal("q99_unknown", invalidAnswerList[0].QuestionId);
    }

    #endregion
}
