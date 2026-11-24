using System;
using System.Text.Json.Serialization;

namespace Andril.Social.Entities;

/// <summary>
/// Minimal offline domain entity representing a single question answer within a parent questionnaire response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Privacy and Ownership Boundaries:</b><br />
/// Questionnaire answers are strictly private to the user's Social profile.
/// <see cref="ResponseId"/> represents an association key linking this answer to its parent response session;
/// it is NOT proof of authorized Social profile ownership. Profile ownership, membership, adult eligibility,
/// and module access are enforced exclusively by the parent response and trusted authorization layer.
/// Answer values (<see cref="QuestionId"/>, <see cref="OptionId"/>) are confidential and must never be
/// logged, exposed via <see cref="ToString"/>, or serialized into public Explore candidate search cards.
/// </para>
/// <para>
/// <b>Validation Scope:</b><br />
/// This entity enforces structural invariants (non-empty identifiers, non-blank strings, and explicit UTC timestamps).
/// Semantic catalog validation (validating question/option existence for a specific questionnaire version,
/// duplicate question prevention, and completion rules) is governed by <c>QuestionnaireValidator</c>.
/// </para>
/// </remarks>
public sealed class QuestionnaireAnswerEntity : IEquatable<QuestionnaireAnswerEntity>
{
    private QuestionnaireAnswerEntity() { }

    /// <summary>
    /// Gets the unique identifier of this questionnaire answer.
    /// </summary>
    [JsonIgnore]
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets the unique identifier of the parent questionnaire response session.
    /// </summary>
    [JsonIgnore]
    public Guid ResponseId { get; private set; }

    /// <summary>
    /// Gets the question identifier (preserved exactly as supplied).
    /// </summary>
    [JsonIgnore]
    public string QuestionId { get; private set; } = null!;

    /// <summary>
    /// Gets the selected option identifier (preserved exactly as supplied).
    /// </summary>
    [JsonIgnore]
    public string OptionId { get; private set; } = null!;

    /// <summary>
    /// Gets the explicit UTC timestamp recording when the answer was selected.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset AnsweredAtUtc { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="QuestionnaireAnswerEntity"/> class with validated structural invariants.
    /// </summary>
    /// <param name="id">The unique identifier for this answer. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="responseId">The parent questionnaire response identifier. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="questionId">The question identifier. Must not be null, empty, or whitespace.</param>
    /// <param name="optionId">The selected option identifier. Must not be null, empty, or whitespace.</param>
    /// <param name="answeredAtUtc">The explicit UTC timestamp. Must be finite, non-default, and have zero offset.</param>
    /// <exception cref="ArgumentException">Thrown when GUIDs are empty, strings are blank, or the timestamp offset is non-zero.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the timestamp is default, minimum, or maximum.</exception>
    public QuestionnaireAnswerEntity(
        Guid id,
        Guid responseId,
        string questionId,
        string optionId,
        DateTimeOffset answeredAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Answer identity cannot be empty.", nameof(id));
        }

        if (responseId == Guid.Empty)
        {
            throw new ArgumentException("Parent response identity cannot be empty.", nameof(responseId));
        }

        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("Question identifier cannot be null, empty, or whitespace.", nameof(questionId));
        }

        if (string.IsNullOrWhiteSpace(optionId))
        {
            throw new ArgumentException("Option identifier cannot be null, empty, or whitespace.", nameof(optionId));
        }

        if (answeredAtUtc == default || answeredAtUtc == DateTimeOffset.MinValue || answeredAtUtc == DateTimeOffset.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(answeredAtUtc),
                answeredAtUtc,
                "Answered timestamp must be a valid, explicit timestamp.");
        }

        if (answeredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Answered timestamp must be explicitly in UTC (zero offset).", nameof(answeredAtUtc));
        }

        Id = id;
        ResponseId = responseId;
        QuestionId = questionId;
        OptionId = optionId;
        AnsweredAtUtc = answeredAtUtc;
    }

    /// <inheritdoc />
    public bool Equals(QuestionnaireAnswerEntity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Id == other.Id &&
               ResponseId == other.ResponseId &&
               string.Equals(QuestionId, other.QuestionId, StringComparison.Ordinal) &&
               string.Equals(OptionId, other.OptionId, StringComparison.Ordinal) &&
               AnsweredAtUtc.Equals(other.AnsweredAtUtc);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is QuestionnaireAnswerEntity other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Id, ResponseId, QuestionId, OptionId, AnsweredAtUtc);

    /// <summary>
    /// Compares two instances for equality.
    /// </summary>
    public static bool operator ==(QuestionnaireAnswerEntity? left, QuestionnaireAnswerEntity? right) =>
        Equals(left, right);

    /// <summary>
    /// Compares two instances for inequality.
    /// </summary>
    public static bool operator !=(QuestionnaireAnswerEntity? left, QuestionnaireAnswerEntity? right) =>
        !Equals(left, right);

    /// <summary>
    /// Returns a non-sensitive diagnostic string representation concealing confidential question and option values.
    /// </summary>
    public override string ToString() =>
        "QuestionnaireAnswerEntity [private]";
}
