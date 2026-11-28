using Andril.Common.Accounts;
using Andril.Economy.Wallet;
using Andril.Shared.Authentication;

namespace Andril.API.Authorization.Economy;

public sealed class AccountBackedWalletAccountResolver(
    ICurrentUser currentUser,
    IAccountRepository accounts) : IWalletAccountResolver
{
    public async Task<Guid> ResolveAccountIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();

        var account = await accounts.FindBySubjectAsync(subject, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!currentUser.IsAuthenticated || currentUser.UserId != subject)
            throw new AccountAuthenticationRequiredException();
        if (account is null || account.Id == Guid.Empty || account.SupabaseUserId != subject)
            throw new InvalidCoinOperationException("An initialized wallet account is required.");

        return account.Id;
    }
}
