using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;

namespace Andril.Groups.Services;

/// <summary>
/// Immutable, privacy-safe result of a scoped group membership lookup operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security and Privacy Boundaries:</b><br />
/// A successful or present membership result represents validated persistence state only.
/// All properties are annotated with <see cref="JsonIgnoreAttribute"/> to prevent accidental
/// transport serialization of private membership existence, group or profile identifiers, module scope,
/// internal membership entities, or validation status. Serialization attributes are a defense-in-depth
/// safeguard and do NOT replace authoritative backend authorization.
/// It does NOT authenticate caller sessions, prove caller identity ownership, or grant access permissions.
/// </para>
/// </remarks>
public sealed class GroupMembershipLookupResult : IEquatable<GroupMembershipLookupResult>
{
    /// <summary>Gets the validated group membership, or <see langword="null"/> if absent or rejected.</summary>
    [JsonIgnore]
    public GroupMembership? Membership { get; }

    /// <summary>Gets the scope validation outcome.</summary>
    [JsonIgnore]
    public GroupMembershipReadScopeValidation Validation { get; }

    /// <summary>Gets the scope validation status.</summary>
    [JsonIgnore]
    public GroupMembershipReadScopeStatus Status => Validation.Status;

    /// <summary>Gets a value indicating whether the lookup result is structurally valid (valid membership or valid absent state).</summary>
    [JsonIgnore]
    public bool IsValid => Validation.IsValid;

    /// <summary>Gets a value indicating whether an active membership was verified within scope.</summary>
    [JsonIgnore]
    public bool HasMembership => Validation.HasMembership && Membership is not null;

    private GroupMembershipLookupResult(GroupMembership? membership, GroupMembershipReadScopeValidation validation)
    {
        Membership = membership;
        Validation = validation;
    }

    /// <summary>Creates a successful lookup result with a validated active membership.</summary>
    public static GroupMembershipLookupResult Found(GroupMembership membership, GroupMembershipReadScopeValidation validation) =>
        new(membership, validation);

    /// <summary>Creates a valid lookup result where no membership was recorded within scope.</summary>
    public static GroupMembershipLookupResult NotFound(GroupMembershipReadScopeValidation validation) =>
        new(null, validation);

    /// <summary>Creates a failed lookup result due to identity or scope boundary violations.</summary>
    public static GroupMembershipLookupResult Rejected(GroupMembershipReadScopeValidation validation) =>
        new(null, validation);

    public bool Equals(GroupMembershipLookupResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Equals(Membership, other.Membership) && Validation.Equals(other.Validation);
    }

    public override bool Equals(object? obj) =>
        obj is GroupMembershipLookupResult other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Membership, Validation);

    public override string ToString() =>
        $"GroupMembershipLookupResult [Status={Status}]";
}

/// <summary>
/// Internal coordinator for validating retrieved group membership records against requested group, module scope, and profile identity (CF-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// This service coordinates repository retrieval and scope validation only. It does NOT:
/// <list type="bullet">
/// <item><description>Grant group access permissions, administrator rights, or moderator roles.</description></item>
/// <item><description>Authenticate caller sessions or verify caller profile ownership.</description></item>
/// <item><description>Bypass member admission rules or zone join-request approvals.</description></item>
/// <item><description>Expose cross-module group memberships or profile identities.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class GroupMembershipScopedLookupService
{
    private readonly IGroupMembershipRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupMembershipScopedLookupService"/> class.
    /// </summary>
    /// <param name="repository">The scoped group membership repository.</param>
    public GroupMembershipScopedLookupService(IGroupMembershipRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Asynchronously retrieves and validates a group membership against the requested group identity, module scope, and member profile.
    /// Fails closed if repository returns an unvalidated, mismatched, or foreign record.
    /// </summary>
    /// <param name="groupId">The requested group identifier.</param>
    /// <param name="scope">The requested group module scope.</param>
    /// <param name="memberProfileId">The requested member profile identifier.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A deterministic, privacy-safe <see cref="GroupMembershipLookupResult"/>.</returns>
    public async Task<GroupMembershipLookupResult> LookupMembershipAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid memberProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (groupId == Guid.Empty || memberProfileId == Guid.Empty)
        {
            var failValidation = GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.InvalidIdentity);
            return GroupMembershipLookupResult.Rejected(failValidation);
        }

        if (scope is null)
        {
            var nullValidation = GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.NullInput);
            return GroupMembershipLookupResult.Rejected(nullValidation);
        }

        var membership = await _repository.GetMembershipAsync(groupId, scope, memberProfileId, cancellationToken);

        var validation = GroupMembershipReadScopePolicy.Validate(groupId, scope, memberProfileId, membership);

        if (!validation.IsValid)
        {
            return GroupMembershipLookupResult.Rejected(validation);
        }

        if (!validation.HasMembership || membership is null)
        {
            return GroupMembershipLookupResult.NotFound(validation);
        }

        return GroupMembershipLookupResult.Found(membership, validation);
    }

    /// <summary>
    /// Asynchronously determines whether an active, valid membership exists within the specified scope.
    /// </summary>
    /// <param name="groupId">The requested group identifier.</param>
    /// <param name="scope">The requested group module scope.</param>
    /// <param name="memberProfileId">The requested member profile identifier.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns><c>true</c> if a valid active membership exists within scope; otherwise, <c>false</c>.</returns>
    public async Task<bool> HasActiveMembershipAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid memberProfileId,
        CancellationToken cancellationToken = default)
    {
        var result = await LookupMembershipAsync(groupId, scope, memberProfileId, cancellationToken);
        return result.HasMembership;
    }
}
