using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;

namespace Andril.Social.Interfaces;

/// <summary>
/// Internal persistence repository contract defining atomic Social questionnaire response lifecycle operations.
/// Supports overwrite-protected initial creation, concurrency-safe revision replacement, active retrieval, and withdrawal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b>
/// <list type="bullet">
/// <item><description>All operations are strictly scoped to an authorized Social <c>profileId</c>.</description></item>
/// <item><description>Initial response creation must fail closed if an active response already exists.</description></item>
/// <item><description>Replacement requires the exact identity of the existing response to prevent race conditions and unintended overwrites.</description></item>
/// <item><description>Withdrawal deactivates active questionnaire participation without leaking confidential historical answers.</description></item>
/// </list>
/// </para>
/// </remarks>
public interface ISocialQuestionnaireLifecycleRepository
{
    /// <summary>
    /// Asynchronously retrieves the currently active questionnaire response for an authorized Social profile.
    /// </summary>
    /// <param name="profileId">The unique identifier of the owning Social profile scope.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>The active <see cref="QuestionnaireResponseEntity"/>, or <see langword="null"/> if no active response exists.</returns>
    Task<QuestionnaireResponseEntity?> GetActiveResponseAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously creates the initial questionnaire response for an authorized Social profile.
    /// Fails closed if an active questionnaire response already exists for this profile.
    /// </summary>
    /// <param name="profileId">The unique identifier of the owning Social profile scope.</param>
    /// <param name="initialResponse">The initial questionnaire response entity to persist.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A task representing the asynchronous create operation.</returns>
    Task CreateInitialResponseAsync(
        Guid profileId,
        QuestionnaireResponseEntity initialResponse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously replaces an existing questionnaire response with a new revision for an authorized Social profile.
    /// Fails closed if the existing response does not match <paramref name="existingResponseId"/>.
    /// </summary>
    /// <param name="profileId">The unique identifier of the owning Social profile scope.</param>
    /// <param name="existingResponseId">The unique identifier of the existing questionnaire response being superseded.</param>
    /// <param name="newResponse">The new questionnaire response revision to persist.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A task representing the asynchronous replace operation.</returns>
    Task ReplaceCurrentResponseAsync(
        Guid profileId,
        Guid existingResponseId,
        QuestionnaireResponseEntity newResponse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously withdraws or deactivates an active questionnaire response for an authorized Social profile.
    /// </summary>
    /// <param name="profileId">The unique identifier of the owning Social profile scope.</param>
    /// <param name="responseId">The unique identifier of the questionnaire response to withdraw.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A task representing the asynchronous withdrawal operation.</returns>
    Task WithdrawResponseAsync(
        Guid profileId,
        Guid responseId,
        CancellationToken cancellationToken = default);
}
