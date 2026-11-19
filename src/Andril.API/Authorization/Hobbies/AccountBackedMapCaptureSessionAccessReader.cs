
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts;
using Andril.Shared.Authentication;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.Places;

namespace Andril.API.Authorization.Hobbies;

/// <summary>
/// Resolves the authenticated contributor's active Hobbies
/// profile and verifies canonical attraction eligibility.
/// </summary>
public sealed class AccountBackedMapCaptureSessionAccessReader
    : IMapCaptureSessionAccessReader
{
    private readonly ICurrentUser _currentUser;

    private readonly IAccountRepository
        _accountRepository;

    private readonly IHobbiesGalleryProfileAccessReader
        _profileAccessReader;

    private readonly IMapContributionAttractionAccessReader
        _attractionAccessReader;

    public AccountBackedMapCaptureSessionAccessReader(
        ICurrentUser currentUser,
        IAccountRepository accountRepository,
        IHobbiesGalleryProfileAccessReader profileAccessReader,
        IMapContributionAttractionAccessReader attractionAccessReader)
    {
        _currentUser = currentUser
            ?? throw new ArgumentNullException(
                nameof(currentUser));

        _accountRepository = accountRepository
            ?? throw new ArgumentNullException(
                nameof(accountRepository));

        _profileAccessReader = profileAccessReader
            ?? throw new ArgumentNullException(
                nameof(profileAccessReader));

        _attractionAccessReader = attractionAccessReader
            ?? throw new ArgumentNullException(
                nameof(attractionAccessReader));
    }

    public async Task<Guid?> ResolveAuthorizedProfileIdAsync(
        Guid attractionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Reject invalid attraction identifiers.
        if (attractionId == Guid.Empty)
        {
            return null;
        }

        // Resolve the verified authentication subject.
        if (!_currentUser.IsAuthenticated
     || _currentUser.UserId is not Guid subjectId
     || subjectId == Guid.Empty)
        {
            return null;
        }

        // Resolve the persisted internal account.
        var account =
            await _accountRepository.FindBySubjectAsync(
                subjectId,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (account is null
            || account.Id == Guid.Empty
            || account.SupabaseUserId != subjectId)
        {
            return null;
        }

        // Resolve the active Hobbies profile owned
        // by the internal account.
        var profileId =
            await _profileAccessReader
                .FindActiveOwnedHobbiesProfileIdAsync(
                    account.Id,
                    cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (!profileId.HasValue
            || profileId.Value == Guid.Empty)
        {
            return null;
        }

        // Validate the attraction against authoritative
        // backend data for this contributor.
        var attractionIsEligible =
            await _attractionAccessReader
                .IsEligibleForContributionAsync(
                    attractionId,
                    profileId.Value,
                    cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (!attractionIsEligible)
        {
            return null;
        }

        // Return the authorized module profile identity,
        // never the account ID or authentication subject.
        return profileId.Value;
    }
}
