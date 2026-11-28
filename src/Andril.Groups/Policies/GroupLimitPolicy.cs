using Andril.Groups.Entities;

namespace Andril.Groups.Policies;

public enum GroupEntitlementStanding
{
    Unknown,
    Free,
    Premium,
    Downgraded
}

public sealed record GroupLimits(int? MaximumActiveCreatedGroupsPerModule, int MembershipCapacity)
{
    public bool CanCreate(int activeCreatedInModule) =>
        activeCreatedInModule >= 0 &&
        (MaximumActiveCreatedGroupsPerModule is not int maximum || activeCreatedInModule < maximum);

    public bool CanAdmit(int activeMembers) => activeMembers >= 0 && activeMembers < MembershipCapacity;
}

/// <summary>The commercial limits approved in decision AND-M5-LEAD-CF07-LIMITS-20261128-01.</summary>
public static class GroupLimitPolicy
{
    public const int FreeCreationQuota = 5;
    public const int FreeCapacity = 50;
    public const int PremiumCapacity = 100;

    private static readonly GroupLimits FreeLimits = new(FreeCreationQuota, FreeCapacity);
    private static readonly GroupLimits PremiumLimits = new(null, PremiumCapacity);

    public static GroupLimits? Resolve(GroupModuleScope? scope, GroupEntitlementStanding standing) =>
        scope is null ? null : standing switch
        {
            GroupEntitlementStanding.Free or GroupEntitlementStanding.Downgraded => FreeLimits,
            GroupEntitlementStanding.Premium => PremiumLimits,
            _ => null
        };

    internal static GroupEntitlementStanding FromClubTier(HobbiesClubEntitlementTier tier) => tier switch
    {
        HobbiesClubEntitlementTier.Free => GroupEntitlementStanding.Free,
        HobbiesClubEntitlementTier.Premium => GroupEntitlementStanding.Premium,
        HobbiesClubEntitlementTier.ExpiredPremium => GroupEntitlementStanding.Downgraded,
        _ => GroupEntitlementStanding.Unknown
    };
}
