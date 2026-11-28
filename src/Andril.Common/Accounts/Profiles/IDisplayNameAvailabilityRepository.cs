namespace Andril.Common.Accounts.Profiles;

public interface IDisplayNameAvailabilityRepository
{
    Task<bool> IsDisplayNameTakenInModuleAsync(string module, string canonicalName, Guid excludeAccountId, CancellationToken cancellationToken = default);
    Task<bool> IsDisplayNameUsedByAccountAsync(Guid accountId, string canonicalName, string excludeModule, CancellationToken cancellationToken = default);
}
