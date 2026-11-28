using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Persistence.Repositories.Groups;
using Microsoft.EntityFrameworkCore;

namespace Andril.API.Authorization.Groups;

internal static class GroupAvailability
{
    internal static bool IsUnavailable(Exception error) => error is AccountStoreUnavailableException or GroupAuthorityUnavailableException
        or OnboardingDependencyException or PolicyConfigurationException or DbException or DbUpdateException or TimeoutException
        or InvalidOperationException { InnerException: DbException or TimeoutException };
}
