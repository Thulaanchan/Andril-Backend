using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Memberships;

namespace Andril.Groups.Interfaces;

/// <summary>
/// Internal backend persistence repository contract for module-scoped group memberships.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architectural and Security Boundaries:</b>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Internal Abstraction:</b> This interface is an internal persistence contract for the Groups domain,
/// not an external or client-facing API endpoint.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Association vs. Authorization:</b> Caller-supplied identifiers (<c>groupId</c>, <c>memberProfileId</c>)
/// represent entity correlation scopes, NOT proof of authenticated identity, ownership, or permission.
/// Caller-supplied identifiers must never be treated as trusted authorization evidence.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Mandatory Upstream Authorization:</b> Before repository operations are invoked, the authoritative
/// application or service layer must verify authenticated account identity, private module profile ownership,
/// active module eligibility, and group access permissions.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Scoped Isolation:</b> All operations are strictly constrained to a validated group and module scope.
/// Implementations must enforce group and module scoping on all reads and writes to prevent cross-group
/// and cross-module retrieval, overwrites, or data leakage. Unscoped queries and multi-tenant bulk listings
/// are explicitly prohibited.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Failure Safety:</b> Operation cancellation and persistence failures must fail securely without
/// bypassing scope validation or disclosing confidential membership data.
/// </description>
/// </item>
/// </list>
/// </para>
/// </remarks>
public interface IGroupMembershipRepository
{
    /// <summary>
    /// Asynchronously retrieves an internal group membership for a validated group, module scope, and member profile.
    /// </summary>
    /// <param name="groupId">The unique identifier of the target group.</param>
    /// <param name="scope">The validated module scope of the group.</param>
    /// <param name="memberProfileId">The unique identifier of the member profile.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>
    /// A task representing the asynchronous read operation. The task result contains the matching <see cref="GroupMembership"/>,
    /// or <see langword="null"/> if no matching membership exists within the specified group and scope.
    /// </returns>
    Task<GroupMembership?> GetMembershipAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid memberProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously determines whether a membership exists for a validated group, module scope, and member profile.
    /// </summary>
    /// <param name="groupId">The unique identifier of the target group.</param>
    /// <param name="scope">The validated module scope of the group.</param>
    /// <param name="memberProfileId">The unique identifier of the member profile.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns><c>true</c> if a matching membership exists; otherwise, <c>false</c>.</returns>
    Task<bool> ExistsAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid memberProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously persists a validated group membership entity within the group's module scope.
    /// </summary>
    /// <param name="membership">The validated group membership entity to persist.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A task that represents the asynchronous save operation.</returns>
    Task SaveMembershipAsync(
        GroupMembership membership,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously removes a group membership for a validated group, module scope, and member profile.
    /// </summary>
    /// <param name="groupId">The unique identifier of the target group.</param>
    /// <param name="scope">The validated module scope of the group.</param>
    /// <param name="memberProfileId">The unique identifier of the member profile.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns><c>true</c> if the membership was found and removed; otherwise, <c>false</c>.</returns>
    Task<bool> RemoveMembershipAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid memberProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously counts the number of memberships in a specific group within the validated module scope.
    /// </summary>
    /// <param name="groupId">The unique identifier of the target group.</param>
    /// <param name="scope">The validated module scope of the group.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>The total number of memberships in the group.</returns>
    Task<int> GetMemberCountAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default);
}
