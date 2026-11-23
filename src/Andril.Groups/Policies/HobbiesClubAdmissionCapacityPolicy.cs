using System;
using Andril.Groups.Entities;

namespace Andril.Groups.Policies;

/// <summary>
/// Status outcome of evaluating Hobbies Club admission capacity.
/// </summary>
public enum HobbiesClubAdmissionCapacityStatus
{
    /// <summary>The club has capacity available; eligible for further admission consideration.</summary>
    Eligible,

    /// <summary>The club has reached or exceeded its membership capacity limit.</summary>
    CapacityReached,

    /// <summary>The module scope is not Hobbies Club or is null/invalid.</summary>
    InvalidScope,

    /// <summary>Entitlement tier evidence is missing, unknown, or undefined.</summary>
    MissingOrInvalidEntitlement,

    /// <summary>The membership count is negative or invalid.</summary>
    InvalidCount
}

/// <summary>
/// Immutable, privacy-safe evaluation result for Hobbies Club admission capacity.
/// Precondition evaluation only; does not enforce transactional admission or modify membership.
/// </summary>
public sealed record HobbiesClubAdmissionCapacityResult
{
    /// <summary>Gets the capacity status outcome.</summary>
    public HobbiesClubAdmissionCapacityStatus Status { get; init; }

    /// <summary>Gets a value indicating whether new admissions can be considered.</summary>
    public bool IsEligible => Status == HobbiesClubAdmissionCapacityStatus.Eligible;

    /// <summary>Gets the effective maximum capacity for the club.</summary>
    public int MaxCapacity { get; init; }

    /// <summary>Gets an optional diagnostic or failure reason.</summary>
    public string Reason { get; init; } = string.Empty;

    public static HobbiesClubAdmissionCapacityResult Eligible(int maxCapacity, string reason = "Club has available capacity.") =>
        new() { Status = HobbiesClubAdmissionCapacityStatus.Eligible, MaxCapacity = maxCapacity, Reason = reason };

    public static HobbiesClubAdmissionCapacityResult CapacityReached(int maxCapacity, string reason = "Club capacity limit has been reached.") =>
        new() { Status = HobbiesClubAdmissionCapacityStatus.CapacityReached, MaxCapacity = maxCapacity, Reason = reason };

    public static HobbiesClubAdmissionCapacityResult InvalidScope(string reason = "Invalid or unsupported group module scope for club admission.") =>
        new() { Status = HobbiesClubAdmissionCapacityStatus.InvalidScope, MaxCapacity = 0, Reason = reason };

    public static HobbiesClubAdmissionCapacityResult MissingOrInvalidEntitlement(string reason = "Missing or invalid entitlement evidence.") =>
        new() { Status = HobbiesClubAdmissionCapacityStatus.MissingOrInvalidEntitlement, MaxCapacity = 0, Reason = reason };

    public static HobbiesClubAdmissionCapacityResult InvalidCount(string reason = "Membership count cannot be negative.") =>
        new() { Status = HobbiesClubAdmissionCapacityStatus.InvalidCount, MaxCapacity = 0, Reason = reason };
}

/// <summary>
/// Enforces Hobbies Club admission capacity and Premium downgrade policies under BRD §07.1.
/// Evaluates whether a Club has capacity to admit new members across all roles.
/// Structural and policy validation only; does not modify membership or transactional state.
/// </summary>
public static class HobbiesClubAdmissionCapacityPolicy
{
    public const int FreeTierCapacity = 50;
    public const int PremiumTierCapacity = 100;

    /// <summary>
    /// Evaluates whether a Hobbies Club can consider admitting additional members based on current total membership count and entitlement standing.
    /// </summary>
    /// <param name="scope">The group module scope, which must be Hobbies Club.</param>
    /// <param name="currentMembershipCount">Current total member count including all roles.</param>
    /// <param name="entitlementTier">Trusted entitlement tier of the club.</param>
    /// <returns>A deterministic evaluation result.</returns>
    public static HobbiesClubAdmissionCapacityResult Evaluate(
        GroupModuleScope? scope,
        int currentMembershipCount,
        HobbiesClubEntitlementTier entitlementTier)
    {
        if (scope is null)
        {
            return HobbiesClubAdmissionCapacityResult.InvalidScope("Group module scope is null.");
        }

        if (!string.Equals(scope.Module, "hobbies", StringComparison.Ordinal) ||
            scope.GroupKind != GroupKind.Club)
        {
            return HobbiesClubAdmissionCapacityResult.InvalidScope("Scope must be Hobbies Club.");
        }

        if (currentMembershipCount < 0)
        {
            return HobbiesClubAdmissionCapacityResult.InvalidCount("Membership count cannot be negative.");
        }

        if (entitlementTier == HobbiesClubEntitlementTier.Unknown ||
            !Enum.IsDefined(typeof(HobbiesClubEntitlementTier), entitlementTier))
        {
            return HobbiesClubAdmissionCapacityResult.MissingOrInvalidEntitlement("Entitlement tier evidence is missing or invalid.");
        }

        int maxCapacity = entitlementTier switch
        {
            HobbiesClubEntitlementTier.Premium => PremiumTierCapacity,
            HobbiesClubEntitlementTier.Free => FreeTierCapacity,
            HobbiesClubEntitlementTier.ExpiredPremium => FreeTierCapacity,
            _ => FreeTierCapacity
        };

        if (currentMembershipCount >= maxCapacity)
        {
            return HobbiesClubAdmissionCapacityResult.CapacityReached(
                maxCapacity,
                $"Club capacity reached ({currentMembershipCount}/{maxCapacity}). Existing members are retained but additional joins are denied.");
        }

        return HobbiesClubAdmissionCapacityResult.Eligible(
            maxCapacity,
            $"Club has capacity available ({currentMembershipCount}/{maxCapacity}).");
    }
}
