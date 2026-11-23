using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Matching;

namespace Andril.Social.Interfaces;

/// <summary>
/// Internal backend persistence repository contract for private Social (Dating) directional interest records.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architectural and Security Boundaries:</b>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Internal Abstraction:</b> This interface is an internal persistence contract for the Social domain,
/// not a public or client-facing API endpoint.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Association vs. Authorization:</b> A caller-supplied pair key (<paramref name="pairKey"/>) or profile identifier
/// (<paramref name="actingProfileId"/>) represents an entity correlation scope, NOT proof of authenticated identity, ownership,
/// or permission. Caller-supplied identifiers must never be treated as trusted authorization evidence.
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
/// <b>Pair-Scoped Isolation:</b> All operations are strictly constrained to a canonical <see cref="SocialProfilePairKey"/> scope.
/// Implementations must enforce pair scoping on all reads and writes to prevent cross-profile retrieval, overwrites,
/// and data leakage. Unscoped queries and multi-tenant bulk listings are explicitly prohibited.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>No Communication Grant:</b> Persistence or retrieval of directional interest decisions does NOT grant chat,
/// messaging, or communication authorization (e.g. M4 chat authorization evidence).
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Failure Safety:</b> Operation cancellation, timeouts, and persistence dependency failures must fail securely without
/// bypassing authorization checks or disclosing private directional interest decisions.
/// </description>
/// </item>
/// </list>
/// </para>
/// </remarks>
public interface ISocialInterestRepository
{
    /// <summary>
    /// Asynchronously retrieves the directional interest recorded by an acting profile within a canonical profile pair.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="actingProfileId">The acting profile identity whose decision is to be retrieved.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>
    /// The matching <see cref="SocialDirectionalInterest"/>, or <see langword="null"/> if no decision has been recorded
    /// by the acting profile within this pair.
    /// </returns>
    Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously determines whether an acting profile has recorded a directional interest decision within a canonical profile pair.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="actingProfileId">The acting profile identity to check.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns><c>true</c> if a decision has been recorded; otherwise, <c>false</c>.</returns>
    Task<bool> HasRecordedInterestAsync(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves both participants' recorded directional interest decisions for a canonical profile pair.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>
    /// A tuple containing the recorded interest for the pair's <see cref="SocialProfilePairKey.FirstProfileId"/>
    /// and <see cref="SocialProfilePairKey.SecondProfileId"/> respectively, where either or both may be <see langword="null"/>.
    /// </returns>
    Task<(SocialDirectionalInterest? FirstInterest, SocialDirectionalInterest? SecondInterest)> GetPairInterestsAsync(
        SocialProfilePairKey pairKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously persists a validated directional interest decision for a participant within a canonical profile pair.
    /// </summary>
    /// <param name="interest">The validated directional interest record to persist.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A task that represents the asynchronous save operation.</returns>
    Task SaveDirectionalInterestAsync(
        SocialDirectionalInterest interest,
        CancellationToken cancellationToken = default);
}
