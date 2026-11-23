using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;

namespace Andril.Groups.Services;

/// <summary>
/// Status outcome of a Hobbies Club admission preflight check.
/// </summary>
public enum HobbiesClubAdmissionPreflightStatus
{
    /// <summary>Club has capacity available and requester is eligible for further authorization consideration.</summary>
    Eligible,

    /// <summary>Requester already holds active membership in the group within scope.</summary>
    AlreadyMember,

    /// <summary>Club membership capacity has been reached for the effective entitlement tier.</summary>
    CapacityReached,

    /// <summary>Identifier or scope is null, empty, or not compatible with Hobbies Club.</summary>
    InvalidIdentityOrScope,

    /// <summary>Target group does not exist within the specified scope.</summary>
    MissingGroup,

    /// <summary>Repository evidence is inconsistent, foreign, or invalid (e.g. negative count or mismatched scope).</summary>
    InconsistentEvidence,

    /// <summary>Entitlement tier evidence is missing, unknown, or undefined.</summary>
    MissingOrInvalidEntitlement,

    /// <summary>A repository or dependency failure occurred.</summary>
    DependencyFailure
}

/// <summary>
/// Immutable, privacy-safe result of a Hobbies Club admission preflight check.
/// Preflight check only; does NOT authorize admission, approve join requests, create memberships, or grant roles.
/// </summary>
public sealed class HobbiesClubAdmissionPreflightResult : IEquatable<HobbiesClubAdmissionPreflightResult>
{
    /// <summary>Gets the preflight check status outcome.</summary>
    [JsonIgnore]
    public HobbiesClubAdmissionPreflightStatus Status { get; }

    /// <summary>Gets a value indicating whether the requester is eligible for further admission consideration.</summary>
    [JsonIgnore]
    public bool IsEligible => Status == HobbiesClubAdmissionPreflightStatus.Eligible;

    /// <summary>Gets the observed membership count at preflight evaluation time.</summary>
    [JsonIgnore]
    public int CurrentMemberCount { get; }

    /// <summary>Gets the effective maximum capacity for the club.</summary>
    [JsonIgnore]
    public int MaxCapacity { get; }

    /// <summary>Gets an optional diagnostic or failure reason.</summary>
    [JsonIgnore]
    public string DiagnosticMessage { get; }

    private HobbiesClubAdmissionPreflightResult(
        HobbiesClubAdmissionPreflightStatus status,
        string diagnosticMessage,
        int currentMemberCount,
        int maxCapacity)
    {
        Status = status;
        DiagnosticMessage = diagnosticMessage ?? string.Empty;
        CurrentMemberCount = currentMemberCount;
        MaxCapacity = maxCapacity;
    }

    public static HobbiesClubAdmissionPreflightResult Create(
        HobbiesClubAdmissionPreflightStatus status,
        string diagnosticMessage,
        int currentMemberCount = 0,
        int maxCapacity = 0) =>
        new(status, diagnosticMessage, currentMemberCount, maxCapacity);

    public bool Equals(HobbiesClubAdmissionPreflightResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Status == other.Status &&
               CurrentMemberCount == other.CurrentMemberCount &&
               MaxCapacity == other.MaxCapacity &&
               string.Equals(DiagnosticMessage, other.DiagnosticMessage, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) =>
        obj is HobbiesClubAdmissionPreflightResult other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Status, CurrentMemberCount, MaxCapacity, DiagnosticMessage);

    /// <summary>
    /// Non-disclosing string representation that protects private identifiers.
    /// </summary>
    public override string ToString() => $"[HobbiesClubAdmissionPreflightResult: Status={Status}]";
}

/// <summary>
/// Internal, read-only preflight coordinator for Hobbies Club admission (CF-07, BRD §07.1).
/// Composes group and membership repositories with <see cref="HobbiesClubAdmissionCapacityPolicy"/>.
/// Precondition check only: does not authorize join requests, mutate memberships, assign roles, or bypass transactional write controls.
/// </summary>
public sealed class HobbiesClubAdmissionPreflightService
{
    private readonly IGroupEntryRepository _groupRepository;
    private readonly IGroupMembershipRepository _membershipRepository;

    public HobbiesClubAdmissionPreflightService(
        IGroupEntryRepository groupRepository,
        IGroupMembershipRepository membershipRepository)
    {
        _groupRepository = groupRepository ?? throw new ArgumentNullException(nameof(groupRepository));
        _membershipRepository = membershipRepository ?? throw new ArgumentNullException(nameof(membershipRepository));
    }

    /// <summary>
    /// Evaluates admission preflight for a requesting profile into a Hobbies Club.
    /// </summary>
    /// <param name="groupId">The group identifier.</param>
    /// <param name="scope">The group module scope, which must be Hobbies Club.</param>
    /// <param name="requestingProfileId">The requesting profile identifier.</param>
    /// <param name="entitlementTier">Trusted entitlement tier of the club.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A privacy-safe admission preflight result.</returns>
    public async Task<HobbiesClubAdmissionPreflightResult> PreflightAdmissionAsync(
        Guid groupId,
        GroupModuleScope? scope,
        Guid requestingProfileId,
        HobbiesClubEntitlementTier entitlementTier,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Validate GroupId and requesting module-profile identity.
        if (groupId == Guid.Empty || requestingProfileId == Guid.Empty)
        {
            return HobbiesClubAdmissionPreflightResult.Create(
                HobbiesClubAdmissionPreflightStatus.InvalidIdentityOrScope,
                "Group ID or requesting profile ID is empty.");
        }

        // 2. Reject non-Hobbies/Club scope.
        if (scope is null ||
            !string.Equals(scope.Module, "hobbies", StringComparison.Ordinal) ||
            scope.GroupKind != GroupKind.Club)
        {
            return HobbiesClubAdmissionPreflightResult.Create(
                HobbiesClubAdmissionPreflightStatus.InvalidIdentityOrScope,
                "Group module scope must be non-null and match Hobbies Club.");
        }

        // Validate entitlement tier evidence
        if (entitlementTier == HobbiesClubEntitlementTier.Unknown ||
            !Enum.IsDefined(typeof(HobbiesClubEntitlementTier), entitlementTier))
        {
            return HobbiesClubAdmissionPreflightResult.Create(
                HobbiesClubAdmissionPreflightStatus.MissingOrInvalidEntitlement,
                "Entitlement tier evidence is missing or invalid.");
        }

        try
        {
            // 3. Retrieve the exact scoped group.
            var group = await _groupRepository.GetGroupAsync(groupId, scope, cancellationToken)
                .ConfigureAwait(false);

            if (group is null)
            {
                return HobbiesClubAdmissionPreflightResult.Create(
                    HobbiesClubAdmissionPreflightStatus.MissingGroup,
                    "Target group was not found within scope.");
            }

            // 4. Reject a foreign or mismatched group record.
            if (group.Id != groupId ||
                !string.Equals(group.Scope?.Module, "hobbies", StringComparison.Ordinal) ||
                group.Scope?.GroupKind != GroupKind.Club ||
                !group.Scope.Equals(scope))
            {
                return HobbiesClubAdmissionPreflightResult.Create(
                    HobbiesClubAdmissionPreflightStatus.InconsistentEvidence,
                    "Group repository returned an entity with mismatched identity or scope.");
            }

            // 5. Check whether the profile already has a valid scoped membership.
            var existingMembership = await _membershipRepository.GetMembershipAsync(
                groupId, scope, requestingProfileId, cancellationToken)
                .ConfigureAwait(false);

            if (existingMembership is not null)
            {
                // Verify membership consistency
                if (existingMembership.GroupId != groupId ||
                    existingMembership.MemberProfileId != requestingProfileId ||
                    !GroupMembershipScopePolicy.IsCompatible(group.Scope, existingMembership.Scope))
                {
                    return HobbiesClubAdmissionPreflightResult.Create(
                        HobbiesClubAdmissionPreflightStatus.InconsistentEvidence,
                        "Membership repository returned an entity with inconsistent scope or identity.");
                }

                return HobbiesClubAdmissionPreflightResult.Create(
                    HobbiesClubAdmissionPreflightStatus.AlreadyMember,
                    "Requesting profile already possesses active membership in this group.");
            }

            // 6. Read the scoped total membership count.
            int count = await _membershipRepository.GetMemberCountAsync(groupId, scope, cancellationToken)
                .ConfigureAwait(false);

            if (count < 0)
            {
                return HobbiesClubAdmissionPreflightResult.Create(
                    HobbiesClubAdmissionPreflightStatus.InconsistentEvidence,
                    "Membership repository reported an invalid negative member count.");
            }

            // 7. Apply the Club capacity policy.
            var capacityResult = HobbiesClubAdmissionCapacityPolicy.Evaluate(scope, count, entitlementTier);

            return capacityResult.Status switch
            {
                HobbiesClubAdmissionCapacityStatus.Eligible =>
                    HobbiesClubAdmissionPreflightResult.Create(
                        HobbiesClubAdmissionPreflightStatus.Eligible,
                        capacityResult.Reason,
                        count,
                        capacityResult.MaxCapacity),

                HobbiesClubAdmissionCapacityStatus.CapacityReached =>
                    HobbiesClubAdmissionPreflightResult.Create(
                        HobbiesClubAdmissionPreflightStatus.CapacityReached,
                        capacityResult.Reason,
                        count,
                        capacityResult.MaxCapacity),

                HobbiesClubAdmissionCapacityStatus.MissingOrInvalidEntitlement =>
                    HobbiesClubAdmissionPreflightResult.Create(
                        HobbiesClubAdmissionPreflightStatus.MissingOrInvalidEntitlement,
                        capacityResult.Reason),

                HobbiesClubAdmissionCapacityStatus.InvalidCount =>
                    HobbiesClubAdmissionPreflightResult.Create(
                        HobbiesClubAdmissionPreflightStatus.InconsistentEvidence,
                        capacityResult.Reason),

                _ =>
                    HobbiesClubAdmissionPreflightResult.Create(
                        HobbiesClubAdmissionPreflightStatus.InvalidIdentityOrScope,
                        capacityResult.Reason)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HobbiesClubAdmissionPreflightResult.Create(
                HobbiesClubAdmissionPreflightStatus.DependencyFailure,
                "A repository dependency failure occurred during admission preflight: " + ex.GetType().Name);
        }
    }
}
