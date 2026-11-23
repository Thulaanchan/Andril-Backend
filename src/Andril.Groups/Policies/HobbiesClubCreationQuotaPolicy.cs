using System;
using Andril.Groups.Entities;

namespace Andril.Groups.Policies;

/// <summary>
/// Status outcome of evaluating Hobbies Club creation quota.
/// </summary>
public enum HobbiesClubCreationQuotaStatus
{
    /// <summary>The creation quota check passed; eligible for further consideration.</summary>
    Eligible,

    /// <summary>The quota of created clubs has been reached for the entitlement tier.</summary>
    QuotaReached,

    /// <summary>The module scope is not Hobbies Club or is null/invalid.</summary>
    InvalidScope,

    /// <summary>Entitlement tier evidence is missing, unknown, or undefined.</summary>
    MissingOrInvalidEntitlement,

    /// <summary>The created club count is negative or invalid.</summary>
    InvalidCount
}

/// <summary>
/// Trusted entitlement tier information for Hobbies Club operations under BRD §07.1.
/// </summary>
public enum HobbiesClubEntitlementTier
{
    /// <summary>Unknown or unverified entitlement.</summary>
    Unknown = 0,

    /// <summary>Free user tier (max 5 created clubs, 50 club capacity).</summary>
    Free = 1,

    /// <summary>Premium user tier (unlimited created clubs, 100 club capacity).</summary>
    Premium = 2,

    /// <summary>Club with expired Premium standing (retains existing members, 50 capacity limit for new joins).</summary>
    ExpiredPremium = 3
}

/// <summary>
/// Immutable, privacy-safe evaluation result for Hobbies Club creation quota.
/// Does not authenticate callers or authorize group creation.
/// </summary>
public sealed record HobbiesClubCreationQuotaResult
{
    /// <summary>Gets the creation quota status outcome.</summary>
    public HobbiesClubCreationQuotaStatus Status { get; init; }

    /// <summary>Gets a value indicating whether the request is eligible under quota rules.</summary>
    public bool IsEligible => Status == HobbiesClubCreationQuotaStatus.Eligible;

    /// <summary>Gets an optional diagnostic or failure reason.</summary>
    public string Reason { get; init; } = string.Empty;

    public static HobbiesClubCreationQuotaResult Eligible(string reason = "Creation quota check passed.") =>
        new() { Status = HobbiesClubCreationQuotaStatus.Eligible, Reason = reason };

    public static HobbiesClubCreationQuotaResult QuotaReached(string reason = "Created club quota has been reached.") =>
        new() { Status = HobbiesClubCreationQuotaStatus.QuotaReached, Reason = reason };

    public static HobbiesClubCreationQuotaResult InvalidScope(string reason = "Invalid or unsupported group module scope for club creation.") =>
        new() { Status = HobbiesClubCreationQuotaStatus.InvalidScope, Reason = reason };

    public static HobbiesClubCreationQuotaResult MissingOrInvalidEntitlement(string reason = "Missing or invalid entitlement evidence.") =>
        new() { Status = HobbiesClubCreationQuotaStatus.MissingOrInvalidEntitlement, Reason = reason };

    public static HobbiesClubCreationQuotaResult InvalidCount(string reason = "Created club count cannot be negative.") =>
        new() { Status = HobbiesClubCreationQuotaStatus.InvalidCount, Reason = reason };
}

/// <summary>
/// Enforces Hobbies Club creation quota policy rules under BRD §07.1.
/// Structural and policy validation only; does not authenticate callers, authorize group creation, or mutate state.
/// </summary>
public static class HobbiesClubCreationQuotaPolicy
{
    public const int FreeTierMaxCreatedClubs = 5;

    /// <summary>
    /// Evaluates whether a user is eligible to create an additional Hobbies Club under the quota limits.
    /// </summary>
    /// <param name="scope">The target group module scope, which must be Hobbies Club.</param>
    /// <param name="createdClubCount">The number of existing clubs created by the user.</param>
    /// <param name="entitlementTier">The trusted entitlement tier for the user.</param>
    /// <returns>A deterministic evaluation result.</returns>
    public static HobbiesClubCreationQuotaResult Evaluate(
        GroupModuleScope? scope,
        int createdClubCount,
        HobbiesClubEntitlementTier entitlementTier)
    {
        if (scope is null)
        {
            return HobbiesClubCreationQuotaResult.InvalidScope("Group module scope is null.");
        }

        if (!string.Equals(scope.Module, "hobbies", StringComparison.Ordinal) ||
            scope.GroupKind != GroupKind.Club)
        {
            return HobbiesClubCreationQuotaResult.InvalidScope("Scope must be Hobbies Club.");
        }

        if (createdClubCount < 0)
        {
            return HobbiesClubCreationQuotaResult.InvalidCount("Created club count cannot be negative.");
        }

        if (entitlementTier == HobbiesClubEntitlementTier.Unknown ||
            !Enum.IsDefined(typeof(HobbiesClubEntitlementTier), entitlementTier))
        {
            return HobbiesClubCreationQuotaResult.MissingOrInvalidEntitlement("Entitlement tier evidence is missing or invalid.");
        }

        if (entitlementTier == HobbiesClubEntitlementTier.Premium)
        {
            return HobbiesClubCreationQuotaResult.Eligible("Premium tier has unlimited created club quota.");
        }

        // Free or ExpiredPremium tier has a maximum of 5 created clubs (0-4 eligible, 5+ reached)
        if (createdClubCount >= FreeTierMaxCreatedClubs)
        {
            return HobbiesClubCreationQuotaResult.QuotaReached($"Quota reached: free tier limit is {FreeTierMaxCreatedClubs} clubs.");
        }

        return HobbiesClubCreationQuotaResult.Eligible("Created club count is within free tier quota.");
    }
}
