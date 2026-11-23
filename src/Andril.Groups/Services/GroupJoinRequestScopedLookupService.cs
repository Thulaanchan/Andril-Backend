using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Policies;

namespace Andril.Groups.Services;

/// <summary>
/// Status outcome of a scoped group join-request lookup operation.
/// </summary>
public enum GroupJoinRequestLookupStatus
{
    /// <summary>A matching join request was retrieved and verified against group, scope, and requester boundaries.</summary>
    Found,

    /// <summary>No matching join request exists within scope (non-disclosing absence; not membership).</summary>
    NotFound,

    /// <summary>Returned join request belongs to a different group identifier.</summary>
    GroupIdMismatch,

    /// <summary>Returned join request belongs to a different module (cross-module reuse rejected).</summary>
    ModuleMismatch,

    /// <summary>Returned join request has an incompatible group kind.</summary>
    GroupKindMismatch,

    /// <summary>Returned join request module scope does not match requested scope.</summary>
    ScopeMismatch,

    /// <summary>Returned join request belongs to a different requesting profile identifier.</summary>
    RequestingProfileIdMismatch,

    /// <summary>One or more requested group or requesting profile identifiers are empty or invalid.</summary>
    InvalidIdentity,

    /// <summary>Target group entry or module scope reference is null.</summary>
    NullInput
}

/// <summary>
/// Immutable, privacy-safe result of a scoped group join-request lookup operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security and Privacy Boundaries:</b><br />
/// A found or absent join-request result represents validated persistence state only.
/// All properties are annotated with <see cref="JsonIgnoreAttribute"/> to prevent accidental
/// transport serialization of private join-request existence, group/profile identifiers, or module scope.
/// Serialization attributes are a defense-in-depth safeguard and do NOT replace authoritative backend authorization.
/// This result does NOT approve admission, assign group roles, grant membership, or authenticate callers.
/// </para>
/// </remarks>
public sealed class GroupJoinRequestLookupResult : IEquatable<GroupJoinRequestLookupResult>
{
    /// <summary>Gets the validated group join request, or <see langword="null"/> if absent or rejected.</summary>
    [JsonIgnore]
    public GroupJoinRequest? JoinRequest { get; }

    /// <summary>Gets the scoped lookup validation status.</summary>
    [JsonIgnore]
    public GroupJoinRequestLookupStatus Status { get; }

    /// <summary>Gets a value indicating whether the lookup succeeded without scope or identity violations.</summary>
    [JsonIgnore]
    public bool IsValid =>
        Status == GroupJoinRequestLookupStatus.Found || Status == GroupJoinRequestLookupStatus.NotFound;

    /// <summary>Gets a value indicating whether a valid join request was verified within scope.</summary>
    [JsonIgnore]
    public bool HasJoinRequest =>
        Status == GroupJoinRequestLookupStatus.Found && JoinRequest is not null;

    private GroupJoinRequestLookupResult(GroupJoinRequest? joinRequest, GroupJoinRequestLookupStatus status)
    {
        JoinRequest = joinRequest;
        Status = status;
    }

    /// <summary>Creates a valid lookup result containing a scope-verified join request.</summary>
    public static GroupJoinRequestLookupResult Found(GroupJoinRequest joinRequest)
    {
        ArgumentNullException.ThrowIfNull(joinRequest);
        return new(joinRequest, GroupJoinRequestLookupStatus.Found);
    }

    /// <summary>Creates a valid lookup result representing non-disclosing absence.</summary>
    public static GroupJoinRequestLookupResult NotFound() =>
        new(null, GroupJoinRequestLookupStatus.NotFound);

    /// <summary>Creates a fail-closed rejected lookup result for identity or scope boundary violations.</summary>
    public static GroupJoinRequestLookupResult Rejected(GroupJoinRequestLookupStatus status)
    {
        if (status == GroupJoinRequestLookupStatus.Found || status == GroupJoinRequestLookupStatus.NotFound)
        {
            throw new ArgumentException("Rejected status cannot be Found or NotFound.", nameof(status));
        }

        return new(null, status);
    }

    public bool Equals(GroupJoinRequestLookupResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Equals(JoinRequest, other.JoinRequest) && Status == other.Status;
    }

    public override bool Equals(object? obj) =>
        obj is GroupJoinRequestLookupResult other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(JoinRequest, Status);

    public override string ToString() =>
        "GroupJoinRequestLookupResult [private]";
}

/// <summary>
/// Internal coordinator for validating retrieved group join-request records against exact group identity,
/// module scope, and requesting private-profile identity (CF-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// This coordinator validates repository read results only. It does NOT:
/// <list type="bullet">
/// <item><description>Approve, reject, or transition join-request lifecycle states.</description></item>
/// <item><description>Grant group membership or assign member, moderator, or admin roles.</description></item>
/// <item><description>Create, update, or delete groups.</description></item>
/// <item><description>Authenticate caller sessions or prove profile ownership.</description></item>
/// <item><description>Expose public API endpoints or disclose unauthorized group existence.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class GroupJoinRequestScopedLookupService
{
    private readonly IGroupJoinRequestRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupJoinRequestScopedLookupService"/> class.
    /// </summary>
    /// <param name="repository">The scoped group join-request repository.</param>
    public GroupJoinRequestScopedLookupService(IGroupJoinRequestRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Asynchronously retrieves and validates a group join request against the exact requested group identity,
    /// module scope, and requesting profile identity. Fails closed on foreign or mismatched repository records.
    /// </summary>
    /// <param name="groupId">The requested group identifier.</param>
    /// <param name="scope">The requested group module scope.</param>
    /// <param name="requestingProfileId">The requested private profile identifier.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A deterministic, privacy-safe <see cref="GroupJoinRequestLookupResult"/>.</returns>
    public async Task<GroupJoinRequestLookupResult> LookupJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid requestingProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (groupId == Guid.Empty || requestingProfileId == Guid.Empty)
        {
            return GroupJoinRequestLookupResult.Rejected(GroupJoinRequestLookupStatus.InvalidIdentity);
        }

        if (scope is null)
        {
            return GroupJoinRequestLookupResult.Rejected(GroupJoinRequestLookupStatus.NullInput);
        }

        var joinRequest = await _repository.GetJoinRequestAsync(groupId, scope, requestingProfileId, cancellationToken);

        if (joinRequest is null)
        {
            return GroupJoinRequestLookupResult.NotFound();
        }

        var scopeValidation = GroupJoinRequestScopePolicy.Validate(groupId, scope, joinRequest);
        if (!scopeValidation.IsValid)
        {
            return GroupJoinRequestLookupResult.Rejected(MapPolicyFailure(scopeValidation.Status));
        }

        if (joinRequest.RequestingProfileId != requestingProfileId)
        {
            return GroupJoinRequestLookupResult.Rejected(GroupJoinRequestLookupStatus.RequestingProfileIdMismatch);
        }

        return GroupJoinRequestLookupResult.Found(joinRequest);
    }

    /// <summary>
    /// Asynchronously retrieves and validates a group join request against the target group entry and requesting profile.
    /// </summary>
    /// <param name="group">The target group entry.</param>
    /// <param name="requestingProfileId">The requested private profile identifier.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A deterministic, privacy-safe <see cref="GroupJoinRequestLookupResult"/>.</returns>
    public Task<GroupJoinRequestLookupResult> LookupJoinRequestAsync(
        GroupEntry group,
        Guid requestingProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (group is null)
        {
            return Task.FromResult(GroupJoinRequestLookupResult.Rejected(GroupJoinRequestLookupStatus.NullInput));
        }

        return LookupJoinRequestAsync(group.Id, group.Scope, requestingProfileId, cancellationToken);
    }

    /// <summary>
    /// Asynchronously determines whether a scope-validated join request exists for the specified group and profile.
    /// </summary>
    /// <param name="groupId">The requested group identifier.</param>
    /// <param name="scope">The requested group module scope.</param>
    /// <param name="requestingProfileId">The requested private profile identifier.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns><c>true</c> if a valid join request exists within scope; otherwise, <c>false</c>.</returns>
    public async Task<bool> HasJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid requestingProfileId,
        CancellationToken cancellationToken = default)
    {
        var result = await LookupJoinRequestAsync(groupId, scope, requestingProfileId, cancellationToken);
        return result.HasJoinRequest;
    }

    private static GroupJoinRequestLookupStatus MapPolicyFailure(GroupJoinRequestScopeStatus status) =>
        status switch
        {
            GroupJoinRequestScopeStatus.GroupIdMismatch => GroupJoinRequestLookupStatus.GroupIdMismatch,
            GroupJoinRequestScopeStatus.ModuleMismatch => GroupJoinRequestLookupStatus.ModuleMismatch,
            GroupJoinRequestScopeStatus.GroupKindMismatch => GroupJoinRequestLookupStatus.GroupKindMismatch,
            GroupJoinRequestScopeStatus.ScopeMismatch => GroupJoinRequestLookupStatus.ScopeMismatch,
            GroupJoinRequestScopeStatus.InvalidIdentity => GroupJoinRequestLookupStatus.InvalidIdentity,
            GroupJoinRequestScopeStatus.NullInput => GroupJoinRequestLookupStatus.NullInput,
            _ => GroupJoinRequestLookupStatus.ScopeMismatch
        };
}
