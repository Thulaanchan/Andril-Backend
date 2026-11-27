
using Andril.Common.Accounts;
using Andril.Hobbies.Interfaces;
using Andril.Shared.Authentication;

namespace Andril.API.Authorization.Hobbies;

internal sealed record GalleryReadScope(Guid SubjectId, Guid AccountId, GalleryActor Actor);

public sealed class AccountBackedGalleryActorResolver
{
    private readonly ICurrentUser _currentUser;
    private readonly IAccountRepository _accounts;
    private readonly IHobbiesGalleryProfileAccessReader _profiles;

    public AccountBackedGalleryActorResolver(
        ICurrentUser currentUser,
        IAccountRepository accounts,
        IHobbiesGalleryProfileAccessReader profiles)
    {
        _currentUser = currentUser
            ?? throw new ArgumentNullException(nameof(currentUser));

        _accounts = accounts
            ?? throw new ArgumentNullException(nameof(accounts));

        _profiles = profiles
            ?? throw new ArgumentNullException(nameof(profiles));
    }

    public async Task<GalleryActor?> ResolveAsync(
        CancellationToken cancellationToken = default) =>
        (await ResolveScopeAsync(cancellationToken))?.Actor;

    internal async Task<GalleryReadScope?> ResolveScopeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_currentUser.IsAuthenticated ||
            _currentUser.UserId is not Guid subjectId ||
            subjectId == Guid.Empty)
        {
            return null;
        }

        var account = await _accounts.FindBySubjectAsync(
            subjectId,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (account is null ||
            account.Id == Guid.Empty ||
            account.SupabaseUserId != subjectId)
        {
            return null;
        }

        var profileId =
            await _profiles.FindActiveOwnedHobbiesProfileIdAsync(
                account.Id,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (profileId is not Guid validProfileId ||
            validProfileId == Guid.Empty)
        {
            return null;
        }

        return new GalleryReadScope(subjectId, account.Id, new GalleryActor(validProfileId, "Hobbies", true));
    }
}
