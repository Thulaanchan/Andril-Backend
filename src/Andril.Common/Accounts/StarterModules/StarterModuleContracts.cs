using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Policies;

namespace Andril.Common.Accounts.StarterModules;

public static class StarterModules
{
    public const string Learning = "learning";
    public const string Hobbies = "hobbies";
    public const string Social = "social";
    public static bool IsValid(string? module) => module is Learning or Hobbies or Social;
}

public sealed record StarterModuleState(AccountDemographics? Demographics,
    IReadOnlyList<AccountPolicyRecord> Policies, StarterMembership? Membership);
public sealed record StarterModuleEligibility(string Module, bool AgeEligible, bool RequiresSocialOptIn, bool CanSelect);
public sealed record StarterMembershipDto(string Module, DateTimeOffset ActivatedAtUtc, bool SocialOptIn);
public sealed record StarterModuleStatus(bool DemographicsComplete, bool RequiredPoliciesComplete,
    IReadOnlyList<StarterModuleEligibility> Modules, StarterMembershipDto? Membership, string NextStep);

public interface IStarterModuleRepository
{
    Task<StarterModuleState> ReadAsync(Guid accountId, CancellationToken cancellationToken = default);
    // Run server business validation inside the transaction after locking current prerequisites.
    Task<StarterModuleState> SelectAsync(Guid accountId, Func<StarterModuleState, StarterMembership> select,
        CancellationToken cancellationToken = default);
}
public interface IStarterModuleService
{
    Task<StarterModuleStatus> GetAsync(CancellationToken cancellationToken = default);
    Task<StarterModuleStatus> SelectAsync(string? module, bool? socialOptIn, CancellationToken cancellationToken = default);
}
public sealed class StarterModuleValidationException : Exception { }
public sealed class StarterModuleAccountMissingException : Exception { }
public sealed class StarterModulePrerequisitesException : Exception { }
public sealed class StarterModuleIneligibleException : Exception { }
public sealed class StarterModuleConflictException : Exception { }
