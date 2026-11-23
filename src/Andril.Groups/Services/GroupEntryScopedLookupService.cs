using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;

namespace Andril.Groups.Services;

/// <summary>
/// Status outcome of a scoped group entry lookup operation.
/// </summary>
public enum GroupEntryLookupStatus
{
    /// <summary>A matching group entry was retrieved and verified against requested group ID, module, and group kind.</summary>
    Found,

    /// <summary>No matching group entry exists within scope (non-disclosing absence).</summary>
    NotFound,

    /// <summary>Returned group entry has a different group identifier than requested.</summary>
    GroupIdMismatch,

    /// <summary>Returned group entry belongs to a different module.</summary>
    ModuleMismatch,

    /// <summary>Returned group entry has a different group kind.</summary>
    GroupKindMismatch,

    /// <summary>Requested or returned module scope is invalid or incompatible.</summary>
    ScopeMismatch,

    /// <summary>Requested or returned group identifier is empty or invalid.</summary>
    InvalidIdentity,

    /// <summary>Requested group module scope reference is null.</summary>
    NullInput
}

/// <summary>
/// Immutable, privacy-safe result of a scoped group entry lookup operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security and Privacy Boundaries:</b><br />
/// A found or absent group result represents validated persistence state only.
/// All properties are annotated with <see cref="JsonIgnoreAttribute"/> to prevent accidental
/// transport serialization of private group existence, group identifiers, names, or module scopes.
/// Serialization attributes are a defense-in-depth safeguard and do NOT replace authoritative backend authorization.
/// This result does NOT grant group membership, assign roles, authenticate callers, or authorize access.
/// </para>
/// </remarks>
public sealed class GroupEntryLookupResult : IEquatable<GroupEntryLookupResult>
{
    /// <summary>Gets the validated group entry, or <see langword="null"/> if absent or rejected.</summary>
    [JsonIgnore]
    public GroupEntry? Group { get; }

    /// <summary>Gets the scoped lookup validation status.</summary>
    [JsonIgnore]
    public GroupEntryLookupStatus Status { get; }

    /// <summary>Gets a value indicating whether the lookup succeeded without scope or identity violations.</summary>
    [JsonIgnore]
    public bool IsValid =>
        Status == GroupEntryLookupStatus.Found || Status == GroupEntryLookupStatus.NotFound;

    /// <summary>Gets a value indicating whether a valid group entry was verified within scope.</summary>
    [JsonIgnore]
    public bool HasGroup =>
        Status == GroupEntryLookupStatus.Found && Group is not null;

    private GroupEntryLookupResult(GroupEntry? group, GroupEntryLookupStatus status)
    {
        Group = group;
        Status = status;
    }

    /// <summary>Creates a valid lookup result containing a scope-verified group entry.</summary>
    public static GroupEntryLookupResult Found(GroupEntry group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new(group, GroupEntryLookupStatus.Found);
    }

    /// <summary>Creates a valid lookup result representing non-disclosing absence.</summary>
    public static GroupEntryLookupResult NotFound() =>
        new(null, GroupEntryLookupStatus.NotFound);

    /// <summary>Creates a fail-closed rejected lookup result for identity or scope boundary violations.</summary>
    public static GroupEntryLookupResult Rejected(GroupEntryLookupStatus status)
    {
        if (status == GroupEntryLookupStatus.Found || status == GroupEntryLookupStatus.NotFound)
        {
            throw new ArgumentException("Rejected status cannot be Found or NotFound.", nameof(status));
        }

        return new(null, status);
    }

    public bool Equals(GroupEntryLookupResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Equals(Group, other.Group) && Status == other.Status;
    }

    public override bool Equals(object? obj) =>
        obj is GroupEntryLookupResult other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Group, Status);

    public override string ToString() =>
        "GroupEntryLookupResult [private]";
}

/// <summary>
/// Internal coordinator for validating retrieved group entries against exact group identity
/// and validated module scope (CF-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// This coordinator validates repository read results only. It does NOT:
/// <list type="bullet">
/// <item><description>Enumerate or list groups across unscoped queries.</description></item>
/// <item><description>Grant group access, membership, or moderator/admin roles.</description></item>
/// <item><description>Create, update, delete, or enforce lifecycle quotas on groups.</description></item>
/// <item><description>Authenticate caller sessions or expose public API endpoints.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class GroupEntryScopedLookupService
{
    private readonly IGroupEntryRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupEntryScopedLookupService"/> class.
    /// </summary>
    /// <param name="repository">The scoped group entry repository.</param>
    public GroupEntryScopedLookupService(IGroupEntryRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Asynchronously retrieves and validates a group entry against the exact requested group identifier
    /// and validated module scope. Fails closed on foreign or wrong-module repository results.
    /// </summary>
    /// <param name="groupId">The requested group identifier.</param>
    /// <param name="scope">The requested group module scope.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A deterministic, privacy-safe <see cref="GroupEntryLookupResult"/>.</returns>
    public async Task<GroupEntryLookupResult> LookupGroupAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (groupId == Guid.Empty)
        {
            return GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.InvalidIdentity);
        }

        if (scope is null)
        {
            return GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.NullInput);
        }

        if (!GroupModuleScopePolicy.IsCompatible(scope.Module, scope.GroupKind))
        {
            return GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.ScopeMismatch);
        }

        var group = await _repository.GetGroupAsync(groupId, scope, cancellationToken);

        if (group is null)
        {
            return GroupEntryLookupResult.NotFound();
        }

        if (group.Id == Guid.Empty)
        {
            return GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.InvalidIdentity);
        }

        if (group.Id != groupId)
        {
            return GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.GroupIdMismatch);
        }

        if (group.Scope is null || !GroupModuleScopePolicy.IsCompatible(group.Scope.Module, group.Scope.GroupKind))
        {
            return GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.ScopeMismatch);
        }

        if (!string.Equals(scope.Module, group.Scope.Module, StringComparison.OrdinalIgnoreCase))
        {
            return GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.ModuleMismatch);
        }

        if (scope.GroupKind != group.Scope.GroupKind)
        {
            return GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.GroupKindMismatch);
        }

        return GroupEntryLookupResult.Found(group);
    }

    /// <summary>
    /// Asynchronously determines whether a scope-validated group entry exists for the specified identifier and module scope.
    /// </summary>
    /// <param name="groupId">The requested group identifier.</param>
    /// <param name="scope">The requested group module scope.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns><c>true</c> if a valid group entry exists within scope; otherwise, <c>false</c>.</returns>
    public async Task<bool> ExistsInScopeAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default)
    {
        var result = await LookupGroupAsync(groupId, scope, cancellationToken);
        return result.HasGroup;
    }
}
