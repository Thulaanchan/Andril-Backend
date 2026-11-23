using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Matching;

namespace Andril.Social.Interfaces;

/// <summary>
/// Internal backend persistence repository contract for module-scoped Social match reads (CF-20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Architectural and Security Boundaries:</b>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Internal Read Abstraction:</b> This interface is an internal persistence contract for the Social domain,
/// not an external or client-facing API endpoint. It provides minimal, read-only entity retrieval.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Association vs. Authorization:</b> A caller-supplied identifier (<c>matchId</c>, <c>requestingProfileId</c>)
/// represents an entity correlation scope, NOT proof of authenticated identity, ownership, or permission.
/// Caller-supplied identifiers must never be treated as trusted authorization evidence.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Mandatory Upstream Authorization:</b> Before repository operations are invoked, the authoritative
/// application or service layer must verify authenticated account identity, private Social profile ownership,
/// active Dating module eligibility, explicit Dating opt-in, adult eligibility (18+), and moderation/block standing.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Scoped Isolation:</b> All operations are strictly constrained to a validated match identity or canonical
/// <see cref="SocialProfilePairKey"/> and requesting profile scope. Unscoped queries, cross-module listings,
/// and public multi-tenant bulk discovery are explicitly prohibited to protect private match confidentiality.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Authorization Failures and Fail-Closed Semantics:</b> Missing records (<see langword="null"/>) differ strictly
/// from authorization denial or unavailable authorization dependencies at the application boundary.
/// Unavailable dependencies must fail closed.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>No Communication Grant:</b> Retrieval of a match entity does NOT prove active match validity or grant chat,
/// messaging, voice, or communication authorization (e.g. M4 conversation access evidence).
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>No Lifecycle Mutation:</b> This read abstraction defines no create, update, delete, or activation methods;
/// lifecycle mutation and retention rules remain subject to separate transaction and retention approval.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Failure Safety:</b> Operation cancellation, timeouts, and persistence dependency failures must fail securely
/// without bypassing authorization checks or disclosing private match records.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>No Production Registration:</b> This interface must remain unregistered in production DI until the persistence
/// adapter and database schema are authoritatively reviewed and approved.
/// </description>
/// </item>
/// </list>
/// </para>
/// </remarks>
public interface ISocialMatchRepository
{
    /// <summary>
    /// Asynchronously retrieves an internal Social match entity by its unique match identifier within an authorized requesting profile scope.
    /// </summary>
    /// <param name="matchId">The unique match identifier to look up.</param>
    /// <param name="requestingProfileId">The unique identifier of the requesting Social profile scope.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>
    /// A task representing the asynchronous read operation. The task result contains the matching <see cref="SocialMatchEntity"/>,
    /// or <see langword="null"/> if no matching record exists within the specified profile scope.
    /// Returning <see langword="null"/> specifically denotes that no record was found; it must not be used to represent
    /// an authorization denial or an unavailable authorization dependency.
    /// </returns>
    Task<SocialMatchEntity?> GetMatchByIdAsync(
        Guid matchId,
        Guid requestingProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves an internal Social match entity for a canonical profile pair within an authorized requesting profile scope.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key to look up.</param>
    /// <param name="requestingProfileId">The unique identifier of the requesting Social profile scope.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>
    /// A task representing the asynchronous read operation. The task result contains the matching <see cref="SocialMatchEntity"/>,
    /// or <see langword="null"/> if no matching record exists within the specified profile scope.
    /// </returns>
    Task<SocialMatchEntity?> GetMatchByPairKeyAsync(
        SocialProfilePairKey pairKey,
        Guid requestingProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously determines whether a Social match record exists for a canonical profile pair.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key to check.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns><c>true</c> if a matching record exists; otherwise, <c>false</c>.</returns>
    Task<bool> HasMatchAsync(
        SocialProfilePairKey pairKey,
        CancellationToken cancellationToken = default);
}
