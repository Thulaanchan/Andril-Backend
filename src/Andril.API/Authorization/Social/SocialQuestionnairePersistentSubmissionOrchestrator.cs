using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.Repositories.Social;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Personality;
using Andril.Social.Services;

namespace Andril.API.Authorization.Social;

/// <summary>
/// Privacy-safe result of an authorized questionnaire submission and persistent save.
/// Conceals private profile IDs, raw question IDs, and answer choices.
/// </summary>
public sealed record SocialQuestionnairePersistentSubmissionResult
{
    public const string CurrentResponseExists = "CURRENT_RESPONSE_EXISTS";

    [JsonIgnore]
    public bool IsSuccess { get; }

    [JsonIgnore]
    public Guid? ResponseId { get; }

    [JsonIgnore]
    public QuestionnaireParticipationStatus Status { get; }

    [JsonIgnore]
    public IReadOnlyList<string> Errors { get; }

    [JsonIgnore]
    public string DiagnosticMessage { get; }

    private SocialQuestionnairePersistentSubmissionResult(
        bool isSuccess,
        Guid? responseId,
        QuestionnaireParticipationStatus status,
        IReadOnlyList<string> errors,
        string diagnosticMessage)
    {
        IsSuccess = isSuccess;
        ResponseId = responseId;
        Status = status;
        Errors = errors ?? Array.Empty<string>();
        DiagnosticMessage = diagnosticMessage ?? string.Empty;
    }

    public static SocialQuestionnairePersistentSubmissionResult Success(
        Guid responseId,
        QuestionnaireParticipationStatus status) =>
        new(true, responseId, status, Array.Empty<string>(), "Questionnaire submitted and persisted successfully within authorized Social profile scope.");

    public static SocialQuestionnairePersistentSubmissionResult ValidationFailed(
        QuestionnaireParticipationStatus status,
        IReadOnlyList<string> errors) =>
        new(false, null, status, errors, "Questionnaire submission failed domain validation.");

    public static SocialQuestionnairePersistentSubmissionResult Rejected(
        string errorCode,
        string diagnosticMessage) =>
        new(false, null, QuestionnaireParticipationStatus.NotStarted, new[] { errorCode }, diagnosticMessage);

    public override string ToString() =>
        $"[SocialQuestionnairePersistentSubmissionResult: IsSuccess={IsSuccess}, Status={Status}]";
}

/// <summary>
/// Service contract for authorized persistent Social questionnaire submissions.
/// </summary>
public interface ISocialQuestionnairePersistentSubmissionOrchestrator
{
    /// <summary>
    /// Validates the caller's authorized Social profile scope, executes domain questionnaire validation,
    /// and atomically persists the resulting response snapshot to the isolated Social schema.
    /// </summary>
    Task<SocialQuestionnairePersistentSubmissionResult> SubmitAndPersistAsync(
        QuestionnaireSubmission? submission,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates authorized Social questionnaire submissions: verifies caller Social profile ownership,
/// validates submission content against the active definition catalog, maps answers to domain entities,
/// and persists snapshots atomically via <see cref="ISocialQuestionnaireLifecycleRepository"/>.
/// Fails closed on unauthenticated or mismatched profile scopes.
/// </summary>
public sealed class SocialQuestionnairePersistentSubmissionOrchestrator : ISocialQuestionnairePersistentSubmissionOrchestrator
{
    private const string SocialModule = "social";

    private readonly IPrivateProfileService _profileService;
    private readonly QuestionnaireDefinitionCatalog _catalog;
    private readonly QuestionnaireApplicationService _questionnaireApplicationService;
    private readonly ISocialQuestionnaireLifecycleRepository _repository;

    public SocialQuestionnairePersistentSubmissionOrchestrator(
        IPrivateProfileService profileService,
        QuestionnaireDefinitionCatalog catalog,
        QuestionnaireApplicationService questionnaireApplicationService,
        ISocialQuestionnaireLifecycleRepository repository)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _questionnaireApplicationService = questionnaireApplicationService ?? throw new ArgumentNullException(nameof(questionnaireApplicationService));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<SocialQuestionnairePersistentSubmissionResult> SubmitAndPersistAsync(
        QuestionnaireSubmission? submission,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await _profileService.WithAuthorizedProfileAsync(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (profile is null || !string.Equals(profile.Module, SocialModule, StringComparison.OrdinalIgnoreCase)
                || profile.ProfileId == Guid.Empty)
            {
                throw new ProfileAccessDeniedException();
            }

            if (submission is null)
            {
                return SocialQuestionnairePersistentSubmissionResult.Rejected(
                    "INVALID_SUBMISSION",
                    "Questionnaire submission payload cannot be null.");
            }

            var validationResult = _questionnaireApplicationService.Validate(submission);
            if (!validationResult.IsValid)
            {
                return SocialQuestionnairePersistentSubmissionResult.ValidationFailed(
                    validationResult.Status,
                    validationResult.Errors);
            }

            var definition = _catalog.FindByVersion(submission.Version);
            if (definition is null)
            {
                return SocialQuestionnairePersistentSubmissionResult.Rejected(
                    "VERSION_MISMATCH",
                    "Questionnaire definition version is not recognized.");
            }

            var responseId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;

            var answerEntities = submission.Answers
                .Select(a => new QuestionnaireAnswerEntity(
                    Guid.NewGuid(),
                    responseId,
                    a.QuestionId,
                    a.OptionId,
                    nowUtc))
                .ToList();

            var responseEntity = QuestionnaireResponseEntity.Create(
                responseId,
                profile.ProfileId,
                definition,
                answerEntities,
                submission.Skip,
                nowUtc);

            try
            {
                await _repository.CreateInitialResponseAsync(profile.ProfileId, responseEntity, cancellationToken).ConfigureAwait(false);
            }
            catch (SocialQuestionnaireCurrentResponseConflictException)
            {
                return SocialQuestionnairePersistentSubmissionResult.Rejected(
                    SocialQuestionnairePersistentSubmissionResult.CurrentResponseExists,
                    "A current questionnaire response already exists.");
            }

            return SocialQuestionnairePersistentSubmissionResult.Success(responseId, responseEntity.Status);
        }, cancellationToken).ConfigureAwait(false);
    }
}
