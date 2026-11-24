using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Interfaces;

namespace Andril.Social.Services;

/// <summary>
/// Status outcome of a scoped questionnaire response lookup.
/// </summary>
public enum SocialQuestionnaireResponseLookupStatus
{
    /// <summary>Matching questionnaire response found and verified strictly within authorized profile scope.</summary>
    Found,

    /// <summary>No matching response exists within the authorized profile scope.</summary>
    NotFound,

    /// <summary>Returned response belongs to a different profile than the authorized scope.</summary>
    ProfileIdMismatch,

    /// <summary>Returned response identity does not match the requested response ID.</summary>
    ResponseIdMismatch,

    /// <summary>Authorized profile ID or response ID is empty or invalid.</summary>
    InvalidIdentity,

    /// <summary>A repository or persistence dependency failure occurred.</summary>
    DependencyFailure
}

/// <summary>
/// Immutable, privacy-safe result of a scoped questionnaire response lookup.
/// Conceals raw answers, identifiers, versions, and timestamps in JSON serialization and diagnostics.
/// Does not grant caller authorization, prove profile ownership, or publish public candidate data.
/// </summary>
public sealed class SocialQuestionnaireResponseLookupResult : IEquatable<SocialQuestionnaireResponseLookupResult>
{
    /// <summary>Gets the validated questionnaire response entity, or <see langword="null"/> if not found or rejected.</summary>
    [JsonIgnore]
    public QuestionnaireResponseEntity? Response { get; }

    /// <summary>Gets the lookup outcome status.</summary>
    [JsonIgnore]
    public SocialQuestionnaireResponseLookupStatus Status { get; }

    /// <summary>Gets a safe, non-disclosing diagnostic message.</summary>
    [JsonIgnore]
    public string DiagnosticMessage { get; }

    /// <summary>Gets a value indicating whether a valid response was found within authorized scope.</summary>
    [JsonIgnore]
    public bool IsFound => Status == SocialQuestionnaireResponseLookupStatus.Found && Response is not null;

    /// <summary>Gets a value indicating whether the record was absent within authorized scope.</summary>
    [JsonIgnore]
    public bool IsNotFound => Status == SocialQuestionnaireResponseLookupStatus.NotFound;

    /// <summary>Gets a value indicating whether the lookup completed without scope violations or dependency failures.</summary>
    [JsonIgnore]
    public bool IsValid => IsFound || IsNotFound;

    private SocialQuestionnaireResponseLookupResult(
        QuestionnaireResponseEntity? response,
        SocialQuestionnaireResponseLookupStatus status,
        string diagnosticMessage)
    {
        Response = response;
        Status = status;
        DiagnosticMessage = diagnosticMessage ?? string.Empty;
    }

    public static SocialQuestionnaireResponseLookupResult Found(QuestionnaireResponseEntity response) =>
        new(response, SocialQuestionnaireResponseLookupStatus.Found, "Questionnaire response found within authorized profile scope.");

    public static SocialQuestionnaireResponseLookupResult NotFound(string message = "No matching response was found within scope.") =>
        new(null, SocialQuestionnaireResponseLookupStatus.NotFound, message);

    public static SocialQuestionnaireResponseLookupResult InvalidIdentity(string message = "Profile ID or response ID is empty or invalid.") =>
        new(null, SocialQuestionnaireResponseLookupStatus.InvalidIdentity, message);

    public static SocialQuestionnaireResponseLookupResult ProfileIdMismatch(string message = "Returned response belongs to a different profile.") =>
        new(null, SocialQuestionnaireResponseLookupStatus.ProfileIdMismatch, message);

    public static SocialQuestionnaireResponseLookupResult ResponseIdMismatch(string message = "Returned response ID does not match the requested ID.") =>
        new(null, SocialQuestionnaireResponseLookupStatus.ResponseIdMismatch, message);

    public static SocialQuestionnaireResponseLookupResult DependencyFailure(string message) =>
        new(null, SocialQuestionnaireResponseLookupStatus.DependencyFailure, message);

    public bool Equals(SocialQuestionnaireResponseLookupResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Status == other.Status &&
               string.Equals(DiagnosticMessage, other.DiagnosticMessage, StringComparison.Ordinal) &&
               Equals(Response?.Id, other.Response?.Id);
    }

    public override bool Equals(object? obj) =>
        obj is SocialQuestionnaireResponseLookupResult other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Status, DiagnosticMessage, Response?.Id);

    /// <summary>
    /// Non-disclosing string representation that conceals confidential profile IDs, answer details, and timestamps.
    /// </summary>
    public override string ToString() => $"[SocialQuestionnaireResponseLookupResult: Status={Status}]";
}

/// <summary>
/// Internal read coordinator for scoped questionnaire response lookups (SOC-01 / Purpose-Based Privacy).
/// Wraps <see cref="IQuestionnairePersistenceRepository"/> with strict fail-closed identity and scope boundaries.
/// Does not authenticate callers, authorize API endpoints, manage replacement policies, or create migrations.
/// </summary>
public sealed class SocialQuestionnaireResponseScopedLookupService
{
    private readonly IQuestionnairePersistenceRepository _repository;

    public SocialQuestionnaireResponseScopedLookupService(IQuestionnairePersistenceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Asynchronously retrieves an internal questionnaire response within an authorized Social profile scope.
    /// </summary>
    /// <param name="authorizedProfileId">The already authorized Social profile identifier scope.</param>
    /// <param name="responseId">The questionnaire response identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A privacy-safe scoped lookup result.</returns>
    public async Task<SocialQuestionnaireResponseLookupResult> LookupResponseAsync(
        Guid authorizedProfileId,
        Guid responseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Require nonempty profile and response IDs.
        if (authorizedProfileId == Guid.Empty || responseId == Guid.Empty)
        {
            return SocialQuestionnaireResponseLookupResult.InvalidIdentity(
                "Authorized profile ID and response ID must be non-empty GUIDs.");
        }

        try
        {
            // 2 & 3. Query GetByIdAsync only within that exact scope.
            var response = await _repository.GetByIdAsync(authorizedProfileId, responseId, cancellationToken)
                .ConfigureAwait(false);

            if (response is null)
            {
                return SocialQuestionnaireResponseLookupResult.NotFound();
            }

            // 4. Check the returned entity's ProfileId.
            if (response.ProfileId != authorizedProfileId)
            {
                return SocialQuestionnaireResponseLookupResult.ProfileIdMismatch(
                    "Repository returned a response with a mismatched profile scope.");
            }

            // 5. Check the returned entity's response Id.
            if (response.Id != responseId)
            {
                return SocialQuestionnaireResponseLookupResult.ResponseIdMismatch(
                    "Repository returned a response with a mismatched response identity.");
            }

            // 6 & 7. Return a privacy-safe internal result.
            return SocialQuestionnaireResponseLookupResult.Found(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 10. Do not swallow authorization or persistence failures as ordinary NotFound.
            return SocialQuestionnaireResponseLookupResult.DependencyFailure(
                "A repository dependency failure occurred: " + ex.GetType().Name);
        }
    }
}
