using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Andril.Social.Personality;
using Andril.Social.Validators;

namespace Andril.Social.Entities;

/// <summary>
/// Minimal offline domain entity representing one private Social questionnaire response snapshot and its associated answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Privacy and Ownership Boundaries:</b><br />
/// Questionnaire responses belong strictly to the user's private Social profile.
/// <see cref="ProfileId"/> represents an association key linking this response to its parent Social profile;
/// it is NOT proof of authorized Social profile ownership. Profile ownership, active module status, adult eligibility (18+),
/// and opt-in authorization are enforced exclusively by the trusted authorization layer.
/// Raw responses and answers are strictly confidential and must never be exposed via <see cref="ToString"/>,
/// serialized into public Explore candidate cards, or shared across modules.
/// </para>
/// <para>
/// <b>Validation Scope:</b><br />
/// This entity enforces structural invariants (non-empty identities, parent/child response correlation, unique answer IDs,
/// and explicit UTC timestamps) and reuses <see cref="QuestionnaireValidator"/> to evaluate submission completeness and status.
/// </para>
/// </remarks>
public sealed class QuestionnaireResponseEntity
{
    private readonly List<QuestionnaireAnswerEntity> _answers = [];
    private readonly IReadOnlyList<QuestionnaireAnswerEntity> _answerView;

    private QuestionnaireResponseEntity() => _answerView = _answers.AsReadOnly();

    private QuestionnaireResponseEntity(
        Guid id,
        Guid profileId,
        string version,
        QuestionnaireParticipationStatus status,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<QuestionnaireAnswerEntity> answers) : this()
    {
        Id = id;
        ProfileId = profileId;
        Version = version;
        Status = status;
        CreatedAtUtc = createdAtUtc;
        _answers.AddRange(answers);
    }

    /// <summary>
    /// Gets the unique identifier of this questionnaire response.
    /// </summary>
    [JsonIgnore]
    public Guid Id { get; private set; }

    /// <summary>
    /// Gets the unique identifier of the associated private Social profile.
    /// </summary>
    [JsonIgnore]
    public Guid ProfileId { get; private set; }

    /// <summary>
    /// Gets the validated questionnaire definition version.
    /// </summary>
    [JsonIgnore]
    public string Version { get; private set; } = null!;

    /// <summary>
    /// Gets the server-derived participation status evaluated by <see cref="QuestionnaireValidator"/>.
    /// </summary>
    [JsonIgnore]
    public QuestionnaireParticipationStatus Status { get; private set; }

    /// <summary>
    /// Gets the explicit UTC timestamp recording when this response snapshot was created.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// Gets the defensive, read-only collection of answers associated with this response.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<QuestionnaireAnswerEntity> Answers => _answerView;

    /// <summary>
    /// Creates a validated, immutable <see cref="QuestionnaireResponseEntity"/> snapshot.
    /// </summary>
    /// <param name="id">The unique identifier for this response. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="profileId">The associated Social profile identifier. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="definition">The questionnaire definition against which the submission is validated.</param>
    /// <param name="answers">The collection of answer entities associated with this response.</param>
    /// <param name="skip">Indicates whether the questionnaire was explicitly skipped.</param>
    /// <param name="createdAtUtc">The explicit UTC timestamp. Must be finite, non-default, and have zero offset.</param>
    /// <returns>A validated <see cref="QuestionnaireResponseEntity"/> instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition"/> or <paramref name="answers"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when identities are empty, answers are invalid or inconsistent, or validation fails.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="createdAtUtc"/> is default, minimum, or maximum.</exception>
    public static QuestionnaireResponseEntity Create(
        Guid id,
        Guid profileId,
        QuestionnaireDefinition definition,
        IEnumerable<QuestionnaireAnswerEntity> answers,
        bool skip,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Response identity cannot be empty.", nameof(id));
        }

        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identity cannot be empty.", nameof(profileId));
        }

        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(answers);

        if (createdAtUtc == default || createdAtUtc == DateTimeOffset.MinValue || createdAtUtc == DateTimeOffset.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAtUtc),
                createdAtUtc,
                "Creation timestamp must be a valid, explicit timestamp.");
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Creation timestamp must be explicitly in UTC (zero offset).",
                nameof(createdAtUtc));
        }

        var definitionValidation = QuestionnaireValidator.ValidateDefinition(definition);
        if (!definitionValidation.IsValid)
        {
            throw new ArgumentException(
                "Questionnaire definition is invalid.",
                nameof(definition));
        }

        var answerList = answers.ToArray();
        var answerIds = new HashSet<Guid>();
        var submissionAnswers = new QuestionnaireAnswer[answerList.Length];

        for (var i = 0; i < answerList.Length; i++)
        {
            var answer = answerList[i];
            if (answer is null)
            {
                throw new ArgumentException("Answer entity cannot be null.", nameof(answers));
            }

            if (answer.ResponseId != id)
            {
                throw new ArgumentException(
                    "Answer response identity does not match parent response identity.",
                    nameof(answers));
            }

            if (!answerIds.Add(answer.Id))
            {
                throw new ArgumentException(
                    "Duplicate answer identity detected.",
                    nameof(answers));
            }

            submissionAnswers[i] = new QuestionnaireAnswer(answer.QuestionId, answer.OptionId);
        }

        var submission = new QuestionnaireSubmission(
            definition.Version,
            Array.AsReadOnly(submissionAnswers),
            skip);

        var validationResult = QuestionnaireValidator.Validate(definition, submission);
        if (!validationResult.IsValid)
        {
            throw new ArgumentException(
                $"Questionnaire response validation failed: {string.Join(", ", validationResult.Errors)}",
                skip && answerList.Length > 0 ? nameof(skip) : nameof(answers));
        }

        return new QuestionnaireResponseEntity(
            id,
            profileId,
            definition.Version,
            validationResult.Status,
            createdAtUtc,
            answerList);
    }

    /// <summary>
    /// Returns a non-sensitive diagnostic string representation concealing confidential response details.
    /// </summary>
    public override string ToString() => "QuestionnaireResponseEntity [private]";
}
