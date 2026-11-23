using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;

namespace Andril.Groups.Interfaces;

/// <summary>
/// Internal backend persistence repository contract for module-scoped group entity reads (CF-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Architectural and Security Boundaries:</b>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Internal Read Abstraction:</b> This interface provides minimal, read-only entity retrieval for module-scoped groups.
/// It does not expose group mutation, deletion, creation quota, moderation, or role management APIs.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Association vs. Authorization:</b> A caller-supplied <c>groupId</c> represents an entity correlation identifier,
/// NOT proof of authenticated identity, ownership, or permission. Caller-supplied identifiers must never be treated as trusted authorization evidence.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Scoped Isolation:</b> All operations are strictly constrained to an explicit group identifier and validated <see cref="GroupModuleScope"/>.
/// Unscoped queries, cross-module searches, and public multi-tenant bulk listings are explicitly prohibited to protect private group confidentiality.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Failure Safety:</b> Operation cancellation and persistence failures must fail securely without
/// bypassing scope validation or disclosing confidential group existence data.
/// </description>
/// </item>
/// </list>
/// </para>
/// </remarks>
public interface IGroupEntryRepository
{
    /// <summary>
    /// Asynchronously retrieves a group entry for an explicit group identifier within a validated module scope.
    /// </summary>
    /// <param name="groupId">The unique identifier of the target group.</param>
    /// <param name="scope">The validated module scope of the group.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>
    /// A task representing the asynchronous read operation. The task result contains the matching <see cref="GroupEntry"/>,
    /// or <see langword="null"/> if no group exists matching both the specified identifier and module scope.
    /// </returns>
    Task<GroupEntry?> GetGroupAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously determines whether a group entry exists matching both the specified identifier and validated module scope.
    /// </summary>
    /// <param name="groupId">The unique identifier of the target group.</param>
    /// <param name="scope">The validated module scope of the group.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns><c>true</c> if a matching group entry exists within scope; otherwise, <c>false</c>.</returns>
    Task<bool> ExistsAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default);
}
