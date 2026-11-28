using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class DisplayNameAvailabilityRepository(AccountsDbContext db) : IDisplayNameAvailabilityRepository
{
    public async Task<bool> IsDisplayNameTakenInModuleAsync(string module, string canonicalName, Guid excludeAccountId, CancellationToken cancellationToken = default)
    {
        try
        {
            var normModule = module.Trim().ToLowerInvariant();
            return await db.PrivateProfiles.AsNoTracking().AnyAsync(
                x => x.Module == normModule && x.CanonicalDisplayName == canonicalName && x.AccountId != excludeAccountId,
                cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<bool> IsDisplayNameUsedByAccountAsync(Guid accountId, string canonicalName, string excludeModule, CancellationToken cancellationToken = default)
    {
        try
        {
            var normExcludeModule = excludeModule.Trim().ToLowerInvariant();
            return await db.PrivateProfiles.AsNoTracking().AnyAsync(
                x => x.AccountId == accountId && x.CanonicalDisplayName == canonicalName && x.Module != normExcludeModule,
                cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }
}
