using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;

namespace Andril.Social.Interfaces;

/// <summary>
/// Internal persistence repository contract for private Social (Dating) personality questionnaire responses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architectural and Security Boundaries:</b>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Internal Abstraction:</b> This interface is an internal backend persistence abstraction for the Social domain,
/// not a public or client-facing API contract.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Association vs. Authorization:</b> Profile identifiers (<c>profileId</c>) represent entity correlation keys linking
/// questionnaire responses to a private Social profile. They are NOT proof of identity, membership, or caller authorization.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Pre-Operation Authorization:</b> Trusted authorization must be performed by authoritative application services
/// before invoking any persistence operation. The authoritative M2 Accounts layer governs account status, active profile access,
/// 18+ adult eligibility, and module opt-in/revocation.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Data Privacy:</b> Raw questionnaire answers, question IDs, option selections, and timestamps are strictly private.
/// Under no circumstances may questionnaire details be leaked to other modules (Learning, Hobbies), exposed in public Explore
/// candidate matching cards, or logged into diagnostic channels.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Profile-Scoped Isolation:</b> All operations are strictly scoped to a single Social profile. Unscoped queries, cross-profile
/// reads, and multi-tenant bulk listings are explicitly prohibited to prevent data leakage.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Failure Safety:</b> Operation cancellation, timeouts, and persistence dependency failures must fail securely without
/// bypassing authorization checks or partially exposing uncommitted state.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Contract Guarantees:</b> This interface defines purely the method contract. It does not establish database uniqueness,
/// transactions, table schemas, or physical consistency constraints on its own.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Deferred Technical Decisions:</b> Specific database persistence mechanisms (e.g., PostgreSQL table schema, EF Core mappings),
/// version uniqueness constraints, concurrent modification handling, questionnaire retention periods, and response replacement
/// lifecycles remain deferred to future M2, M5, and M6 architecture review.
/// </description>
/// </item>
/// </list>
/// </para>
/// </remarks>
public interface IQuestionnairePersistenceRepository
{
    /// <summary>
    /// Asynchronously retrieves an internal questionnaire response for an authorized Social profile by its unique response identity.
    /// </summary>
    /// <param name="profileId">The unique identifier of the owning Social profile scope.</param>
    /// <param name="responseId">The unique identifier of the questionnaire response to retrieve.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>
    /// A task representing the asynchronous read operation. The task result contains the matching <see cref="QuestionnaireResponseEntity"/>,
    /// or <see langword="null"/> if the record is missing, does not belong to the specified profile scope, or is inaccessible.
    /// </returns>
    Task<QuestionnaireResponseEntity?> GetByIdAsync(
        Guid profileId,
        Guid responseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves the current internal questionnaire response associated with an authorized Social profile.
    /// </summary>
    /// <param name="profileId">The unique identifier of the owning Social profile scope.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>
    /// A task representing the asynchronous read operation. The task result contains the current <see cref="QuestionnaireResponseEntity"/>
    /// for the specified profile scope, or <see langword="null"/> if no response has been recorded or it is inaccessible.
    /// </returns>
    Task<QuestionnaireResponseEntity?> GetByProfileIdAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously persists a validated questionnaire response snapshot associated with an authorized Social profile.
    /// </summary>
    /// <param name="profileId">The unique identifier of the owning Social profile scope. Must match <see cref="QuestionnaireResponseEntity.ProfileId"/>.</param>
    /// <param name="response">The validated questionnaire response snapshot entity to persist.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A task that represents the asynchronous save operation.</returns>
    /// <remarks>
    /// Implementations must verify identity consistency between <paramref name="profileId"/> and <paramref name="response"/>,
    /// enforce authorized profile ownership within the persistence boundary, and prevent cross-profile overwrites.
    /// Concrete lifecycle semantics (insert, replace, or versioned append) remain deferred to future persistence schema review.
    /// </remarks>
    Task SaveAsync(
        Guid profileId,
        QuestionnaireResponseEntity response,
        CancellationToken cancellationToken = default);
}
