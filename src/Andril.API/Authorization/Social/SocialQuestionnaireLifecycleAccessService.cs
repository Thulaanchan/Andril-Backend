using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Entities;
using Andril.Social.Interfaces;

namespace Andril.API.Authorization.Social;

/// <summary>
/// Service contract for owner-authorized Social questionnaire lifecycle operations.
/// </summary>
public interface ISocialQuestionnaireLifecycleAccessService
{
    /// <summary>
    /// Asynchronously retrieves the active questionnaire response for the caller's authorized Social profile.
    /// </summary>
    Task<QuestionnaireResponseEntity?> GetActiveResponseAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously creates the initial questionnaire response for the caller's authorized Social profile.
    /// Fails closed if an active response already exists.
    /// </summary>
    Task CreateInitialResponseAsync(
        QuestionnaireResponseEntity initialResponse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously replaces an existing questionnaire response with a new revision for the caller's authorized Social profile.
    /// Fails closed if the existing response ID does not match.
    /// </summary>
    Task ReplaceResponseAsync(
        Guid existingResponseId,
        QuestionnaireResponseEntity newResponse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously withdraws or deactivates an active questionnaire response for the caller's authorized Social profile.
    /// </summary>
    Task WithdrawResponseAsync(
        Guid responseId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates owner-authorized Social questionnaire response lifecycle operations.
/// Enforces fail-closed authentication, module boundaries, profile ownership, and overwrite protection.
/// </summary>
public sealed class SocialQuestionnaireLifecycleAccessService : ISocialQuestionnaireLifecycleAccessService
{
    private const string SocialModule = "social";

    private readonly IPrivateProfileService _profileService;
    private readonly ISocialQuestionnaireLifecycleRepository _lifecycleRepository;

    public SocialQuestionnaireLifecycleAccessService(
        IPrivateProfileService profileService,
        ISocialQuestionnaireLifecycleRepository lifecycleRepository)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _lifecycleRepository = lifecycleRepository ?? throw new ArgumentNullException(nameof(lifecycleRepository));
    }

    public async Task<QuestionnaireResponseEntity?> GetActiveResponseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await _profileService.WithAuthorizedProfileAsync(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProfileScope(profile);

            var response = await _lifecycleRepository.GetActiveResponseAsync(profile.ProfileId, cancellationToken);
            if (response is null)
            {
                return null;
            }

            ValidateResponseOwnership(response, profile.ProfileId);
            return response;
        }, cancellationToken);
    }

    public async Task CreateInitialResponseAsync(
        QuestionnaireResponseEntity initialResponse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialResponse);
        cancellationToken.ThrowIfCancellationRequested();

        await _profileService.WithAuthorizedProfileAsync<bool>(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProfileScope(profile);
            ValidateResponseOwnership(initialResponse, profile.ProfileId);

            await _lifecycleRepository.CreateInitialResponseAsync(profile.ProfileId, initialResponse, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task ReplaceResponseAsync(
        Guid existingResponseId,
        QuestionnaireResponseEntity newResponse,
        CancellationToken cancellationToken = default)
    {
        if (existingResponseId == Guid.Empty)
        {
            throw new ArgumentException("Existing response identity cannot be empty.", nameof(existingResponseId));
        }

        ArgumentNullException.ThrowIfNull(newResponse);
        cancellationToken.ThrowIfCancellationRequested();

        await _profileService.WithAuthorizedProfileAsync<bool>(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProfileScope(profile);
            ValidateResponseOwnership(newResponse, profile.ProfileId);

            await _lifecycleRepository.ReplaceCurrentResponseAsync(
                profile.ProfileId,
                existingResponseId,
                newResponse,
                cancellationToken);

            return true;
        }, cancellationToken);
    }

    public async Task WithdrawResponseAsync(
        Guid responseId,
        CancellationToken cancellationToken = default)
    {
        if (responseId == Guid.Empty)
        {
            throw new ArgumentException("Response identity cannot be empty.", nameof(responseId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        await _profileService.WithAuthorizedProfileAsync<bool>(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProfileScope(profile);

            await _lifecycleRepository.WithdrawResponseAsync(profile.ProfileId, responseId, cancellationToken);
            return true;
        }, cancellationToken);
    }

    private static void ValidateProfileScope(PrivateProfileDto profile)
    {
        if (profile is null ||
            !string.Equals(profile.Module, SocialModule, StringComparison.OrdinalIgnoreCase) ||
            profile.ProfileId == Guid.Empty)
        {
            throw new ProfileAccessDeniedException();
        }
    }

    private static void ValidateResponseOwnership(QuestionnaireResponseEntity response, Guid authorizedProfileId)
    {
        if (response is null ||
            response.ProfileId == Guid.Empty ||
            response.ProfileId != authorizedProfileId ||
            response.Id == Guid.Empty)
        {
            throw new ProfileAccessDeniedException();
        }
    }
}
