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
/// <b>Association vs. Authorization:</b> A caller-supplied profile identifier (<c>profileId</c>) represents an entity
/// correlation scope, NOT proof of authenticated identity, ownership, or permission. Caller-supplied identifiers must never
/// be treated as trusted authorization evidence.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Mandatory Trusted Authorization:</b> Before protected repository operations are invoked, the authoritative application
/// or service layer must verify all required M2 authorization prerequisites:
/// authenticated account identity, private Social profile ownership, active Dating module eligibility, explicit Dating opt-in,
/// adult eligibility (18+), and any relevant restrictions or revocations.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Authorization Failures and Fail-Closed Semantics:</b> Authorization Denied and Authorization Unavailable are distinct
/// outcomes at the trusted application boundary; neither outcome authorizes persistence operations. Unavailable authorization
/// dependencies must fail closed. Upstream authorization failures must not be conflated with or treated as ordinary missing records.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Profile-Scoped Isolation:</b> All operations are strictly constrained to an authorized Social profile scope. Implementations
/// must enforce profile scoping on all reads and writes to prevent cross-profile retrieval, overwrites, and data leakage.
/// Unscoped queries and multi-tenant bulk listings are explicitly prohibited.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Data Privacy Safeguards:</b> Raw questionnaire responses and answers (including question IDs, option selections, and timestamps)
/// are confidential private Social profile data. Under no circumstances may questionnaire details be leaked to other modules (Learning, Hobbies),
/// exposed in public Dating Explore candidate cards, written to unprotected diagnostic logs, or disclosed via unauthorized endpoints.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Failure Safety:</b> Operation cancellation, timeouts, and persistence dependency failures must fail securely without
/// bypassing authorization checks or disclosing private questionnaire information.
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
/// or lifecycle policies remain deferred to future M2, M5, and M6 architecture review.
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
    /// or <see langword="null"/> if no matching response exists within the already authorized Social profile scope.
    /// Returning <see langword="null"/> specifically denotes that no record was found; it must not be used to represent
    /// an authorization denial or an unavailable authorization dependency.
    /// </returns>
    /// <remarks>
    /// Implementations must verify that the requested response belongs strictly to <paramref name="profileId"/>,
    /// returning <see langword="null"/> if the record does not exist or does not match the authorized profile scope.
    /// </remarks>
    Task<QuestionnaireResponseEntity?> GetByIdAsync(
        Guid profileId,
        Guid responseId,
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
    /// Database transactions and uniqueness constraints are not established by this contract alone.
    /// Concrete lifecycle semantics (insert, replace, or versioned append) remain deferred to future persistence schema review.
    /// </remarks>
    Task SaveAsync(
        Guid profileId,
        QuestionnaireResponseEntity response,
        CancellationToken cancellationToken = default);
}
