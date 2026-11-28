namespace Andril.Economy.Wallet;

/// <summary>Resolves the initialized internal account owned by the current authenticated subject.</summary>
public interface IWalletAccountResolver
{
    Task<Guid> ResolveAccountIdAsync(CancellationToken cancellationToken = default);
}
