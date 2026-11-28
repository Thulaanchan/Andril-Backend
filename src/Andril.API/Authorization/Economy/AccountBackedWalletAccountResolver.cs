using Andril.Common.Accounts;
using Andril.Economy.Wallet;

namespace Andril.API.Authorization.Economy;

public sealed class AccountBackedWalletAccountResolver(IAccountService accounts) : IWalletAccountResolver
{
    public async Task<Guid> ResolveAccountIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await accounts.GetMeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (account is null || account.AccountId == Guid.Empty)
            throw new InvalidCoinOperationException("An initialized wallet account is required.");

        return account.AccountId;
    }
}
