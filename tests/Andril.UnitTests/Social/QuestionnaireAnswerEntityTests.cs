using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Social.Entities;
using Xunit;

namespace Andril.UnitTests.Social;

/// <summary>
/// Unit and privacy regression tests for <see cref="QuestionnaireAnswerEntity"/>.
/// Verifies structural invariants, immutability, UTC timestamp boundaries, value equality,
/// default System.Text.Json serialization masking, and safe diagnostic string representation.
/// </summary>
public sealed class QuestionnaireAnswerEntityTests
{
    private static readonly Guid SyntheticAnswerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SyntheticResponseId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string SyntheticQuestionId = "q_synthetic_01";
    private const string SyntheticOptionId = "opt_synthetic_01";
    private static readonly DateTimeOffset SyntheticTimestampUtc =
        new(2026, 11, 22, 12, 30, 45, TimeSpan.Zero);

    private static QuestionnaireAnswerEntity CreateValidEntity(
        Guid? id = null,
        Guid? responseId = null,
        string? questionId = null,
        string? optionId = null,
        DateTimeOffset? answeredAtUtc = null)
    {
        return new QuestionnaireAnswerEntity(
            id ?? SyntheticAnswerId,
            responseId ?? SyntheticResponseId,
            questionId ?? SyntheticQuestionId,
            optionId ?? SyntheticOptionId,
            answeredAtUtc ?? SyntheticTimestampUtc);
    }

    #region A. Valid Construction

    [Fact]
    public void Constructor_WithValidArguments_PopulatesPropertiesExactly()
    {
        var entity = new QuestionnaireAnswerEntity(
            SyntheticAnswerId,
            SyntheticResponseId,
            SyntheticQuestionId,
            SyntheticOptionId,
            SyntheticTimestampUtc);

        Assert.Equal(SyntheticAnswerId, entity.Id);
        Assert.Equal(SyntheticResponseId, entity.ResponseId);
        Assert.Equal(SyntheticQuestionId, entity.QuestionId);
        Assert.Equal(SyntheticOptionId, entity.OptionId);
        Assert.Equal(SyntheticTimestampUtc, entity.AnsweredAtUtc);
    }

    [Fact]
    public void Constructor_PreservesCaseSensitiveAndUnicodeIdentifiersWithoutNormalization()
    {
        const string unicodeQuestionId = "q_Über_CaseSensitive_α";
        const string unicodeOptionId = "opt_Spécial_CaseSensitive_β";

        var entity = CreateValidEntity(
            questionId: unicodeQuestionId,
            optionId: unicodeOptionId);

        Assert.Equal(unicodeQuestionId, entity.QuestionId);
        Assert.Equal(unicodeOptionId, entity.OptionId);
    }

    [Fact]
    public void Constructor_DoesNotGenerateReplacementIdentitiesOrTimestamps()
    {
        var expectedId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var expectedResponseId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var expectedTimestamp = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);

        var entity = new QuestionnaireAnswerEntity(
            expectedId,
            expectedResponseId,
            "q_custom",
            "opt_custom",
            expectedTimestamp);

        Assert.Equal(expectedId, entity.Id);
        Assert.Equal(expectedResponseId, entity.ResponseId);
        Assert.Equal(expectedTimestamp, entity.AnsweredAtUtc);
    }

    #endregion

    #region B. Invalid Answer Identity

    [Fact]
    public void Constructor_EmptyAnswerId_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new QuestionnaireAnswerEntity(
                Guid.Empty,
                SyntheticResponseId,
                SyntheticQuestionId,
                SyntheticOptionId,
                SyntheticTimestampUtc));

        Assert.Equal("id", ex.ParamName);
    }

    #endregion

    #region C. Invalid Parent Response Identity

    [Fact]
    public void Constructor_EmptyResponseId_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new QuestionnaireAnswerEntity(
                SyntheticAnswerId,
                Guid.Empty,
                SyntheticQuestionId,
                SyntheticOptionId,
                SyntheticTimestampUtc));

        Assert.Equal("responseId", ex.ParamName);
    }

    #endregion

    #region D. Invalid Question Identifiers

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("   \t  \r\n  ")]
    public void Constructor_InvalidQuestionId_ThrowsArgumentException(string? invalidQuestionId)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new QuestionnaireAnswerEntity(
                SyntheticAnswerId,
                SyntheticResponseId,
                invalidQuestionId!,
                SyntheticOptionId,
                SyntheticTimestampUtc));

        Assert.Equal("questionId", ex.ParamName);
    }

    [Fact]
    public void Constructor_ValidNonBlankQuestionId_IsPreservedExactly()
    {
        const string validQuestionId = "q_123-valid.custom";
        var entity = CreateValidEntity(questionId: validQuestionId);

        Assert.Equal(validQuestionId, entity.QuestionId);
    }

    #endregion

    #region E. Invalid Option Identifiers

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("   \t  \r\n  ")]
    public void Constructor_InvalidOptionId_ThrowsArgumentException(string? invalidOptionId)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new QuestionnaireAnswerEntity(
                SyntheticAnswerId,
                SyntheticResponseId,
                SyntheticQuestionId,
                invalidOptionId!,
                SyntheticTimestampUtc));

        Assert.Equal("optionId", ex.ParamName);
    }

    [Fact]
    public void Constructor_ValidNonBlankOptionId_IsPreservedExactly()
    {
        const string validOptionId = "opt_456-valid.custom";
        var entity = CreateValidEntity(optionId: validOptionId);

        Assert.Equal(validOptionId, entity.OptionId);
    }

    #endregion

    #region F. UTC Timestamp Validation

    [Fact]
    public void Constructor_DefaultTimestamp_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QuestionnaireAnswerEntity(
                SyntheticAnswerId,
                SyntheticResponseId,
                SyntheticQuestionId,
                SyntheticOptionId,
                default));

        Assert.Equal("answeredAtUtc", ex.ParamName);
    }

    [Fact]
    public void Constructor_MinValueTimestamp_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QuestionnaireAnswerEntity(
                SyntheticAnswerId,
                SyntheticResponseId,
                SyntheticQuestionId,
                SyntheticOptionId,
                DateTimeOffset.MinValue));

        Assert.Equal("answeredAtUtc", ex.ParamName);
    }

    [Fact]
    public void Constructor_MaxValueTimestamp_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QuestionnaireAnswerEntity(
                SyntheticAnswerId,
                SyntheticResponseId,
                SyntheticQuestionId,
                SyntheticOptionId,
                DateTimeOffset.MaxValue));

        Assert.Equal("answeredAtUtc", ex.ParamName);
    }

    [Theory]
    [InlineData(60)]    // +01:00
    [InlineData(330)]   // +05:30
    [InlineData(720)]   // +12:00
    public void Constructor_NonZeroPositiveOffset_ThrowsArgumentException(int offsetMinutes)
    {
        var nonUtc = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.FromMinutes(offsetMinutes));

        var ex = Assert.Throws<ArgumentException>(() =>
            new QuestionnaireAnswerEntity(
                SyntheticAnswerId,
                SyntheticResponseId,
                SyntheticQuestionId,
                SyntheticOptionId,
                nonUtc));

        Assert.Equal("answeredAtUtc", ex.ParamName);
    }

    [Theory]
    [InlineData(-60)]   // -01:00
    [InlineData(-300)]  // -05:00
    [InlineData(-480)]  // -08:00
    public void Constructor_NonZeroNegativeOffset_ThrowsArgumentException(int offsetMinutes)
    {
        var nonUtc = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.FromMinutes(offsetMinutes));

        var ex = Assert.Throws<ArgumentException>(() =>
            new QuestionnaireAnswerEntity(
                SyntheticAnswerId,
                SyntheticResponseId,
                SyntheticQuestionId,
                SyntheticOptionId,
                nonUtc));

        Assert.Equal("answeredAtUtc", ex.ParamName);
    }

    [Fact]
    public void Constructor_ValidExplicitUtcTimestamp_AcceptedAndPreserved()
    {
        var explicitUtc = new DateTimeOffset(2026, 6, 15, 14, 20, 30, TimeSpan.Zero);
        var entity = CreateValidEntity(answeredAtUtc: explicitUtc);

        Assert.Equal(TimeSpan.Zero, entity.AnsweredAtUtc.Offset);
        Assert.Equal(explicitUtc, entity.AnsweredAtUtc);
    }

    #endregion

    #region G. Controlled Mutation / Immutability

    [Fact]
    public void Properties_DoNotHavePublicSetters()
    {
        var properties = typeof(QuestionnaireAnswerEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(5, properties.Length);

        foreach (var property in properties)
        {
            var publicSetter = property.GetSetMethod(nonPublic: false);
            Assert.Null(publicSetter);
        }
    }

    [Fact]
    public void Entity_DoesNotExposeUnexpectedPublicMutationMethods()
    {
        var publicMethods = typeof(QuestionnaireAnswerEntity)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName) // filter out property getters
            .Select(method => method.Name)
            .ToList();

        // Allowed declared public instance methods: Equals (IEquatable & Object), GetHashCode, ToString
        var unexpectedMethods = publicMethods
            .Where(name => name != nameof(Equals) &&
                           name != nameof(GetHashCode) &&
                           name != nameof(ToString))
            .ToList();

        Assert.Empty(unexpectedMethods);
    }

    [Fact]
    public void Entity_ValuesRemainUnchangedAfterRepeatedReads()
    {
        var entity = CreateValidEntity();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(SyntheticAnswerId, entity.Id);
            Assert.Equal(SyntheticResponseId, entity.ResponseId);
            Assert.Equal(SyntheticQuestionId, entity.QuestionId);
            Assert.Equal(SyntheticOptionId, entity.OptionId);
            Assert.Equal(SyntheticTimestampUtc, entity.AnsweredAtUtc);
        }
    }

    #endregion

    #region H. Value Equality

    [Fact]
    public void Equals_SameInstance_ReturnsTrue()
    {
        var entity = CreateValidEntity();
        var sameReference = entity;

        Assert.True(entity.Equals(entity));
        Assert.True(entity == sameReference);
        Assert.False(entity != sameReference);
    }

    [Fact]
    public void Equals_SeparateInstancesWithIdenticalValues_ReturnsTrue()
    {
        var entity1 = CreateValidEntity();
        var entity2 = CreateValidEntity();

        Assert.True(entity1.Equals(entity2));
        Assert.True(entity2.Equals(entity1));
        Assert.True(entity1 == entity2);
        Assert.False(entity1 != entity2);
    }

    [Fact]
    public void GetHashCode_EqualInstances_ProduceEqualHashCodes()
    {
        var entity1 = CreateValidEntity();
        var entity2 = CreateValidEntity();

        Assert.Equal(entity1.GetHashCode(), entity2.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentId_ReturnsFalse()
    {
        var entity1 = CreateValidEntity(id: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var entity2 = CreateValidEntity(id: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        Assert.False(entity1.Equals(entity2));
        Assert.False(entity1 == entity2);
        Assert.True(entity1 != entity2);
    }

    [Fact]
    public void Equals_DifferentResponseId_ReturnsFalse()
    {
        var entity1 = CreateValidEntity(responseId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var entity2 = CreateValidEntity(responseId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        Assert.False(entity1.Equals(entity2));
        Assert.False(entity1 == entity2);
        Assert.True(entity1 != entity2);
    }

    [Fact]
    public void Equals_DifferentQuestionId_ReturnsFalse()
    {
        var entity1 = CreateValidEntity(questionId: "q_first");
        var entity2 = CreateValidEntity(questionId: "q_second");

        Assert.False(entity1.Equals(entity2));
        Assert.False(entity1 == entity2);
        Assert.True(entity1 != entity2);
    }

    [Fact]
    public void Equals_DifferentOptionId_ReturnsFalse()
    {
        var entity1 = CreateValidEntity(optionId: "opt_first");
        var entity2 = CreateValidEntity(optionId: "opt_second");

        Assert.False(entity1.Equals(entity2));
        Assert.False(entity1 == entity2);
        Assert.True(entity1 != entity2);
    }

    [Fact]
    public void Equals_DifferentTimestamp_ReturnsFalse()
    {
        var entity1 = CreateValidEntity(answeredAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var entity2 = CreateValidEntity(answeredAtUtc: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.False(entity1.Equals(entity2));
        Assert.False(entity1 == entity2);
        Assert.True(entity1 != entity2);
    }

    [Fact]
    public void Equals_NullEntity_ReturnsFalse()
    {
        var entity = CreateValidEntity();

        Assert.False(entity.Equals((QuestionnaireAnswerEntity?)null));
        Assert.False(entity.Equals((object?)null));
    }

    [Fact]
    public void Equals_ObjectOverload_MatchingInstance_ReturnsTrue()
    {
        var entity1 = CreateValidEntity();
        object entity2 = CreateValidEntity();

        Assert.True(entity1.Equals(entity2));
    }

    [Fact]
    public void Equals_ObjectOverload_UnrelatedType_ReturnsFalse()
    {
        var entity = CreateValidEntity();

        Assert.False(entity.Equals("unrelated_string"));
        Assert.False(entity.Equals(12345));
        Assert.False(entity.Equals(new object()));
    }

    [Fact]
    public void EqualityOperators_WithNullOperands_BehaveConsistently()
    {
        var entity = CreateValidEntity();

        Assert.True((QuestionnaireAnswerEntity?)null == (QuestionnaireAnswerEntity?)null);
        Assert.False(entity == (QuestionnaireAnswerEntity?)null);
        Assert.False((QuestionnaireAnswerEntity?)null == entity);

        Assert.False((QuestionnaireAnswerEntity?)null != (QuestionnaireAnswerEntity?)null);
        Assert.True(entity != (QuestionnaireAnswerEntity?)null);
        Assert.True((QuestionnaireAnswerEntity?)null != entity);
    }

    [Fact]
    public void Equals_QuestionAndOptionIds_AreCaseSensitive()
    {
        var entityLowerQ = CreateValidEntity(questionId: "question_one");
        var entityUpperQ = CreateValidEntity(questionId: "QUESTION_ONE");

        var entityLowerOpt = CreateValidEntity(optionId: "option_one");
        var entityUpperOpt = CreateValidEntity(optionId: "OPTION_ONE");

        Assert.False(entityLowerQ.Equals(entityUpperQ));
        Assert.False(entityLowerOpt.Equals(entityUpperOpt));
    }

    #endregion

    #region I. Default JSON Serialization Privacy

    [Fact]
    public void JsonSerialization_DefaultOptions_DoesNotExposeConfidentialValues()
    {
        var entity = CreateValidEntity();

        var json = JsonSerializer.Serialize(entity);

        Assert.Equal("{}", json);
        Assert.DoesNotContain(SyntheticAnswerId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticResponseId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticQuestionId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticOptionId, json, StringComparison.Ordinal);
        Assert.DoesNotContain("AnsweredAtUtc", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2026", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Properties_AllHaveJsonIgnoreAttribute()
    {
        var properties = typeof(QuestionnaireAnswerEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(5, properties.Length);

        foreach (var property in properties)
        {
            var ignoreAttribute = property.GetCustomAttribute<JsonIgnoreAttribute>();
            Assert.NotNull(ignoreAttribute);
        }
    }

    [Fact]
    public void JsonSerialization_WithCustomNamingPolicy_StillDoesNotExposeValues()
    {
        var entity = CreateValidEntity();
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(entity, options);

        // Even with camelCase naming policy, JsonIgnore ensures the object is empty
        Assert.DoesNotContain(SyntheticAnswerId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticResponseId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticQuestionId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticOptionId, json, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonSerialization_DoesNotMutateEntityState()
    {
        var entity = CreateValidEntity();

        _ = JsonSerializer.Serialize(entity);

        Assert.Equal(SyntheticAnswerId, entity.Id);
        Assert.Equal(SyntheticResponseId, entity.ResponseId);
        Assert.Equal(SyntheticQuestionId, entity.QuestionId);
        Assert.Equal(SyntheticOptionId, entity.OptionId);
        Assert.Equal(SyntheticTimestampUtc, entity.AnsweredAtUtc);
    }

    #endregion

    #region J. ToString Privacy

    [Fact]
    public void ToString_ReturnsSafeConstantPrivateRepresentation()
    {
        var entity = CreateValidEntity();

        var stringRepresentation = entity.ToString();

        Assert.Equal("QuestionnaireAnswerEntity [private]", stringRepresentation);
    }

    [Fact]
    public void ToString_DoesNotDiscloseInternalIdentitiesOrPayloads()
    {
        var entity = CreateValidEntity();
        var stringRepresentation = entity.ToString();

        Assert.DoesNotContain(SyntheticAnswerId.ToString(), stringRepresentation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticResponseId.ToString(), stringRepresentation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticQuestionId, stringRepresentation, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticOptionId, stringRepresentation, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", stringRepresentation, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_MultipleDistinctInstances_ProduceIdenticalSafeRepresentation()
    {
        var entity1 = CreateValidEntity(
            id: Guid.NewGuid(),
            responseId: Guid.NewGuid(),
            questionId: "q_confidential_alpha",
            optionId: "opt_secret_1");

        var entity2 = CreateValidEntity(
            id: Guid.NewGuid(),
            responseId: Guid.NewGuid(),
            questionId: "q_confidential_bravo",
            optionId: "opt_secret_2");

        Assert.Equal(entity1.ToString(), entity2.ToString());
        Assert.Equal("QuestionnaireAnswerEntity [private]", entity1.ToString());
    }

    #endregion

    #region K. Semantic Validation Boundary

    [Fact]
    public void Entity_AcceptsSyntheticIdentifiersWithoutCatalogOrSemanticValidation()
    {
        // Demonstrates that the leaf entity enforces only structural invariants.
        // Catalog presence and semantic questionnaire completion rules remain the responsibility of QuestionnaireValidator.
        const string uncatalogedQuestionId = "q_not_in_any_catalog_999";
        const string uncatalogedOptionId = "opt_not_in_any_catalog_999";

        var entity = new QuestionnaireAnswerEntity(
            SyntheticAnswerId,
            SyntheticResponseId,
            uncatalogedQuestionId,
            uncatalogedOptionId,
            SyntheticTimestampUtc);

        Assert.Equal(uncatalogedQuestionId, entity.QuestionId);
        Assert.Equal(uncatalogedOptionId, entity.OptionId);
    }

    #endregion
}
