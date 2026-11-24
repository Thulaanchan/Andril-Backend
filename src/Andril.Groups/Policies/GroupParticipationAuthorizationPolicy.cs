using System;
using System.Text.Json.Serialization;
using Andril.Groups.Entities;

namespace Andril.Groups.Policies;

/// <summary>
/// Status outcome of the group participation authorization evaluation.
/// </summary>
public enum GroupParticipationAuthorizationStatus
{
    /// <summary>
    /// Caller profile satisfies module boundary, group kind compatibility, and age criteria.
    /// </summary>
    Authorized,

    /// <summary>
    /// Caller profile module does not match the target group's module boundary.
    /// Cross-module participation is strictly prohibited.
    /// </summary>
    ModuleMismatch,

    /// <summary>
    /// Caller is under 18 years of age attempting to access or join a Social Zone.
    /// </summary>
    UnderageDenied,

    /// <summary>
    /// Group kind and module scope are incompatible or invalid.
    /// </summary>
    InvalidScope,

    /// <summary>
    /// Caller profile identity is empty or malformed.
    /// </summary>
    InvalidProfile,

    /// <summary>
    /// Group identity or scope is null, empty, or malformed.
    /// </summary>
    InvalidGroup,

    /// <summary>
    /// Target group is closed and does not permit participation or new joins.
    /// </summary>
    ClosedGroup,

    /// <summary>
    /// Authoritative server-side evidence is missing or indeterminate.
    /// </summary>
    MissingEvidence,

    /// <summary>
    /// Profile or account is inactive, suspended, or under moderation restriction.
    /// </summary>
    AccountRestricted,

    /// <summary>
    /// Authoritative lifecycle evidence has expired or timestamp is invalid.
    /// </summary>
    StaleEvidence,

    /// <summary>
    /// Caller lacks the required privileged role capability for this operation.
    /// </summary>
    PrivilegeDenied
}

/// <summary>
/// Immutable, privacy-preserving outcome of a group participation authorization evaluation.
/// </summary>
public sealed class GroupParticipationAuthorizationResult : IEquatable<GroupParticipationAuthorizationResult>
{
    /// <summary>
    /// Gets the status outcome of the evaluation.
    /// </summary>
    [JsonIgnore]
    public GroupParticipationAuthorizationStatus Status { get; }

    /// <summary>
    /// Gets the validated group kind.
    /// </summary>
    [JsonIgnore]
    public GroupKind GroupKind { get; }

    /// <summary>
    /// Gets the validated module name.
    /// </summary>
    [JsonIgnore]
    public string Module { get; }

    /// <summary>
    /// Gets a value indicating whether the caller is authorized to participate.
    /// </summary>
    [JsonIgnore]
    public bool IsAuthorized => Status == GroupParticipationAuthorizationStatus.Authorized;

    /// <summary>
    /// Gets a value indicating whether participation was denied.
    /// </summary>
    [JsonIgnore]
    public bool IsDenied => !IsAuthorized;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupParticipationAuthorizationResult"/> class.
    /// </summary>
    public GroupParticipationAuthorizationResult(
        GroupParticipationAuthorizationStatus status,
        GroupKind groupKind,
        string module)
    {
        Status = status;
        GroupKind = groupKind;
        Module = module ?? string.Empty;
    }

    /// <inheritdoc />
    public bool Equals(GroupParticipationAuthorizationResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Status == other.Status
            && GroupKind == other.GroupKind
            && string.Equals(Module, other.Module, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is GroupParticipationAuthorizationResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine((int)Status, (int)GroupKind, Module.ToLowerInvariant());

    /// <inheritdoc />
    public override string ToString() => "GroupParticipationAuthorizationResult [private]";
}

/// <summary>
/// Domain policy enforcing strict module boundaries, group kind alignment, and age eligibility
/// for group participation across Classroom (Learning), Club (Hobbies), and Zone (Social).
/// </summary>
/// <remarks>
/// <para>
/// <b>Module Isolation Invariants:</b>
/// <list type="bullet">
/// <item><description><b>Classroom (Learning):</b> Only callers with an authorized Learning profile can participate. Hobbies and Social profiles are denied.</description></item>
/// <item><description><b>Club (Hobbies):</b> Only callers with an authorized Hobbies profile can participate. Learning and Social profiles are denied.</description></item>
/// <item><description><b>Zone (Social):</b> Only callers with an authorized Social profile can participate. Learning and Hobbies profiles are denied.</description></item>
/// <item><description><b>Social Zone Adult Gate:</b> Social Zone participation strictly requires the caller to be at least 18 years of age.</description></item>
/// <item><description><b>Fail Closed:</b> Malformed, empty, or mismatched scopes fail closed with denial.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class GroupParticipationAuthorizationPolicy
{
    public const string LearningModule = "learning";
    public const string HobbiesModule = "hobbies";
    public const string SocialModule = "social";

    public const int SocialZoneMinimumAge = 18;

    /// <summary>
    /// Evaluates whether a caller profile is authorized to participate in a specified group scope.
    /// </summary>
    /// <param name="groupScope">The validated module and kind scope of the target group.</param>
    /// <param name="groupId">The unique identifier of the target group.</param>
    /// <param name="callerModule">The module context of the caller's authorized profile.</param>
    /// <param name="callerProfileId">The unique identifier of the caller's profile.</param>
    /// <param name="callerAge">The age of the caller in whole years.</param>
    /// <param name="isGroupOpen">Whether the target group is currently open for participation/membership.</param>
    /// <returns>A deterministic <see cref="GroupParticipationAuthorizationResult"/>.</returns>
    public static GroupParticipationAuthorizationResult EvaluateParticipation(
        GroupModuleScope? groupScope,
        Guid groupId,
        string? callerModule,
        Guid callerProfileId,
        int callerAge = 25,
        bool isGroupOpen = true)
    {
        // 1. Group identity and scope validation
        if (groupScope is null || groupId == Guid.Empty)
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.InvalidGroup,
                default,
                string.Empty);
        }

        // 2. Caller profile identity validation
        if (callerProfileId == Guid.Empty || string.IsNullOrWhiteSpace(callerModule))
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.InvalidProfile,
                groupScope.GroupKind,
                groupScope.Module);
        }

        // 3. Group kind and module compatibility validation
        if (!GroupModuleScopePolicy.IsCompatible(groupScope.Module, groupScope.GroupKind))
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.InvalidScope,
                groupScope.GroupKind,
                groupScope.Module);
        }

        // 4. Strict cross-module isolation enforcement
        // Classroom (Learning)
        if (groupScope.GroupKind == GroupKind.Classroom)
        {
            if (!string.Equals(callerModule, LearningModule, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(groupScope.Module, LearningModule, StringComparison.OrdinalIgnoreCase))
            {
                return new GroupParticipationAuthorizationResult(
                    GroupParticipationAuthorizationStatus.ModuleMismatch,
                    groupScope.GroupKind,
                    groupScope.Module);
            }
        }
        // Club (Hobbies)
        else if (groupScope.GroupKind == GroupKind.Club)
        {
            if (!string.Equals(callerModule, HobbiesModule, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(groupScope.Module, HobbiesModule, StringComparison.OrdinalIgnoreCase))
            {
                return new GroupParticipationAuthorizationResult(
                    GroupParticipationAuthorizationStatus.ModuleMismatch,
                    groupScope.GroupKind,
                    groupScope.Module);
            }
        }
        // Zone (Social)
        else if (groupScope.GroupKind == GroupKind.Zone)
        {
            if (!string.Equals(callerModule, SocialModule, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(groupScope.Module, SocialModule, StringComparison.OrdinalIgnoreCase))
            {
                return new GroupParticipationAuthorizationResult(
                    GroupParticipationAuthorizationStatus.ModuleMismatch,
                    groupScope.GroupKind,
                    groupScope.Module);
            }

            // Adult eligibility check for Social Zones
            if (callerAge < SocialZoneMinimumAge)
            {
                return new GroupParticipationAuthorizationResult(
                    GroupParticipationAuthorizationStatus.UnderageDenied,
                    groupScope.GroupKind,
                    groupScope.Module);
            }
        }
        else
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.InvalidScope,
                groupScope.GroupKind,
                groupScope.Module);
        }

        // 5. Open / closed group check
        if (!isGroupOpen)
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.ClosedGroup,
                groupScope.GroupKind,
                groupScope.Module);
        }

        // 6. Authorized
        return new GroupParticipationAuthorizationResult(
            GroupParticipationAuthorizationStatus.Authorized,
            groupScope.GroupKind,
            groupScope.Module);
    }

    /// <summary>
    /// Maximum allowable age of authoritative evidence before it is considered stale.
    /// </summary>
    public static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Strict authoritative evaluation requiring explicitly resolved server-side evidence.
    /// Does not treat default values as authoritative. Fails closed on missing, contradictory, or stale evidence.
    /// </summary>
    /// <param name="profileEvidence">Authoritative profile identity and standing evidence.</param>
    /// <param name="groupEvidence">Authoritative group existence and state evidence.</param>
    /// <param name="requestedScope">The requested group module scope.</param>
    /// <param name="requestedGroupId">The requested group identifier.</param>
    /// <param name="currentUtc">Current trusted server UTC time.</param>
    /// <param name="requiredCapability">Optional privileged capability required for the operation.</param>
    /// <param name="membershipEvidence">Optional authoritative membership and capability evidence.</param>
    /// <returns>A deterministic <see cref="GroupParticipationAuthorizationResult"/>.</returns>
    public static GroupParticipationAuthorizationResult EvaluateStrictAuthorization(
        AuthoritativeGroupProfileEvidence? profileEvidence,
        AuthoritativeGroupStateEvidence? groupEvidence,
        GroupModuleScope? requestedScope,
        Guid requestedGroupId,
        DateTimeOffset currentUtc,
        GroupPrivilegedCapability requiredCapability = GroupPrivilegedCapability.None,
        AuthoritativeGroupMembershipEvidence? membershipEvidence = null)
    {
        // 1. Missing authoritative evidence
        if (profileEvidence is null || groupEvidence is null)
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.MissingEvidence,
                requestedScope?.GroupKind ?? default,
                requestedScope?.Module ?? string.Empty);
        }

        // 2. Verified private module-profile identity
        if (profileEvidence.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(profileEvidence.Module))
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.InvalidProfile,
                groupEvidence.Scope.GroupKind,
                groupEvidence.Scope.Module);
        }

        // 3. Group identity and scope correlation
        if (requestedGroupId == Guid.Empty || groupEvidence.GroupId == Guid.Empty || requestedGroupId != groupEvidence.GroupId)
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.InvalidGroup,
                groupEvidence.Scope.GroupKind,
                groupEvidence.Scope.Module);
        }

        if (requestedScope is null || !requestedScope.Equals(groupEvidence.Scope) || !GroupModuleScopePolicy.IsCompatible(groupEvidence.Scope.Module, groupEvidence.Scope.GroupKind))
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.InvalidScope,
                groupEvidence.Scope.GroupKind,
                groupEvidence.Scope.Module);
        }

        // 4. Freshness of lifecycle evidence
        if (currentUtc < profileEvidence.EvidenceTimestampUtc || (currentUtc - profileEvidence.EvidenceTimestampUtc) > MaximumEvidenceAge ||
            currentUtc < groupEvidence.EvidenceTimestampUtc || (currentUtc - groupEvidence.EvidenceTimestampUtc) > MaximumEvidenceAge)
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.StaleEvidence,
                groupEvidence.Scope.GroupKind,
                groupEvidence.Scope.Module);
        }

        if (membershipEvidence is not null)
        {
            if (currentUtc < membershipEvidence.EvidenceTimestampUtc || (currentUtc - membershipEvidence.EvidenceTimestampUtc) > MaximumEvidenceAge)
            {
                return new GroupParticipationAuthorizationResult(
                    GroupParticipationAuthorizationStatus.StaleEvidence,
                    groupEvidence.Scope.GroupKind,
                    groupEvidence.Scope.Module);
            }
        }

        // 5. Account and profile standing (active, not restricted/moderated)
        if (!profileEvidence.IsActive || profileEvidence.IsRestricted)
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.AccountRestricted,
                groupEvidence.Scope.GroupKind,
                groupEvidence.Scope.Module);
        }

        // 6. Explicit authoritative group state (active)
        if (!groupEvidence.IsActive)
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.InvalidGroup,
                groupEvidence.Scope.GroupKind,
                groupEvidence.Scope.Module);
        }

        // 7. Strict cross-module isolation
        if (!string.Equals(profileEvidence.Module, groupEvidence.Scope.Module, StringComparison.OrdinalIgnoreCase))
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.ModuleMismatch,
                groupEvidence.Scope.GroupKind,
                groupEvidence.Scope.Module);
        }

        var moduleMatchesKind = groupEvidence.Scope.GroupKind switch
        {
            GroupKind.Classroom => string.Equals(profileEvidence.Module, LearningModule, StringComparison.OrdinalIgnoreCase),
            GroupKind.Club => string.Equals(profileEvidence.Module, HobbiesModule, StringComparison.OrdinalIgnoreCase),
            GroupKind.Zone => string.Equals(profileEvidence.Module, SocialModule, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

        if (!moduleMatchesKind)
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.ModuleMismatch,
                groupEvidence.Scope.GroupKind,
                groupEvidence.Scope.Module);
        }

        // 8. Verified adult eligibility for Social Zones (explicit age evidence required)
        if (groupEvidence.Scope.GroupKind == GroupKind.Zone)
        {
            if (!profileEvidence.VerifiedAge.HasValue)
            {
                return new GroupParticipationAuthorizationResult(
                    GroupParticipationAuthorizationStatus.MissingEvidence,
                    groupEvidence.Scope.GroupKind,
                    groupEvidence.Scope.Module);
            }

            if (profileEvidence.VerifiedAge.Value < SocialZoneMinimumAge)
            {
                return new GroupParticipationAuthorizationResult(
                    GroupParticipationAuthorizationStatus.UnderageDenied,
                    groupEvidence.Scope.GroupKind,
                    groupEvidence.Scope.Module);
            }
        }

        // 9. Closed group check
        if (!groupEvidence.IsOpen)
        {
            return new GroupParticipationAuthorizationResult(
                GroupParticipationAuthorizationStatus.ClosedGroup,
                groupEvidence.Scope.GroupKind,
                groupEvidence.Scope.Module);
        }

        // 10. Privileged capabilities for operations requiring specific roles/privileges
        if (requiredCapability != GroupPrivilegedCapability.None)
        {
            if (membershipEvidence is null || !membershipEvidence.IsMember ||
                membershipEvidence.GroupId != groupEvidence.GroupId ||
                membershipEvidence.ProfileId != profileEvidence.ProfileId ||
                !membershipEvidence.Capabilities.HasFlag(requiredCapability))
            {
                return new GroupParticipationAuthorizationResult(
                    GroupParticipationAuthorizationStatus.PrivilegeDenied,
                    groupEvidence.Scope.GroupKind,
                    groupEvidence.Scope.Module);
            }
        }

        // 11. Fully authorized
        return new GroupParticipationAuthorizationResult(
            GroupParticipationAuthorizationStatus.Authorized,
            groupEvidence.Scope.GroupKind,
            groupEvidence.Scope.Module);
    }
}

/// <summary>
/// Authoritative capabilities for privileged group operations.
/// </summary>
[Flags]
public enum GroupPrivilegedCapability
{
    /// <summary>No privileged capability.</summary>
    None = 0,

    /// <summary>Authorized to approve or reject pending join requests.</summary>
    ApproveJoinRequest = 1,

    /// <summary>Authorized to manage group members.</summary>
    ManageMembers = 2,

    /// <summary>Authorized to manage group settings.</summary>
    ManageGroup = 4
}

/// <summary>
/// Authoritative server-resolved evidence of a private module-profile's standing and identity.
/// </summary>
public sealed record AuthoritativeGroupProfileEvidence(
    Guid ProfileId,
    string Module,
    bool IsActive,
    bool IsRestricted,
    int? VerifiedAge,
    DateTimeOffset EvidenceTimestampUtc);

/// <summary>
/// Authoritative server-resolved evidence of target group existence, module scope, and open status.
/// </summary>
public sealed record AuthoritativeGroupStateEvidence(
    Guid GroupId,
    GroupModuleScope Scope,
    bool IsActive,
    bool IsOpen,
    DateTimeOffset EvidenceTimestampUtc);

/// <summary>
/// Authoritative server-resolved evidence of group membership standing and privileged capabilities.
/// </summary>
public sealed record AuthoritativeGroupMembershipEvidence(
    Guid GroupId,
    Guid ProfileId,
    bool IsMember,
    GroupPrivilegedCapability Capabilities,
    DateTimeOffset EvidenceTimestampUtc);

