
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;

namespace Andril.AI.Services;

/// <summary>
/// Internal guard for AI-PRIV-02. Resolves a fresh authorized module actor
/// and requires explicit satisfaction of the privacy notice gate.
/// Does not record consent, grant contextual recommendation permission,
/// or replace atomic admission/dispatch and revocation controls.
/// </summary>
public sealed class AiPrivacyNoticeGuard
{
    private readonly IAiActorAuthorization _authorization;
    private readonly IAiPrivacyNoticeGate _privacyGate;

    public AiPrivacyNoticeGuard(
        IAiActorAuthorization authorization,
        IAiPrivacyNoticeGate privacyGate)
    {
        _authorization = authorization ??
            throw new ArgumentNullException(nameof(authorization));

        _privacyGate = privacyGate ??
            throw new ArgumentNullException(nameof(privacyGate));
    }

    /// <summary>
    /// Initial check for the requested module. Account and profile IDs
    /// are resolved by trusted server authorization, not the client.
    /// </summary>
    public Task<AiAuthorizedActor> RequireSatisfiedAsync(
        AiModule module,
        CancellationToken cancellationToken = default) =>
        RequireCoreAsync(module, null, cancellationToken);

    /// <summary>
    /// Recheck a previously admitted server-side actor before processing.
    /// Never silently switch accounts, profiles or modules. The expected
    /// actor is a comparison reference, not an authorization credential.
    /// </summary>
    public async Task<AiAuthorizedActor> RequireSameActorSatisfiedAsync(
        AiAuthorizedActor? expectedActor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (expectedActor is null || !IsValidActor(expectedActor))
            throw Denied();

        return await RequireCoreAsync(
            expectedActor.Module, expectedActor, cancellationToken);
    }

    private async Task<AiAuthorizedActor> RequireCoreAsync(
        AiModule module,
        AiAuthorizedActor? expectedActor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Enum.IsDefined(module))
            throw Denied();

        var actor = await _authorization.GetAuthorizedActorAsync(
            module, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null ||
            !IsValidActor(actor) ||
            actor.Module != module ||
            (expectedActor is not null && actor != expectedActor))
        {
            throw Denied();
        }

        // The gate independently verifies current scoped privacy evidence
        // and eligibility. Never infer permission from an earlier success.
        var outcome = await _privacyGate.EvaluateAsync(
            actor, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        switch (outcome)
        {
            case AiPrivacyNoticeGateOutcome.Satisfied:
                return actor;

            case AiPrivacyNoticeGateOutcome.NoticeRequired:
            case AiPrivacyNoticeGateOutcome.ConsentRequired:
            case AiPrivacyNoticeGateOutcome.Denied:
                throw Denied();

            case AiPrivacyNoticeGateOutcome.PolicyNotVerified:
                throw new InvalidOperationException(
                    "AI privacy policy could not be verified.");

            default:
                throw new InvalidOperationException(
                    "AI privacy gate outcome could not be verified.");
        }
    }

    private static bool IsValidActor(AiAuthorizedActor actor) =>
        actor.UserId != Guid.Empty &&
        actor.ModuleProfileId != Guid.Empty &&
        Enum.IsDefined(actor.Module);

    private static UnauthorizedAccessException Denied() =>
        new("AI processing is not authorized.");
}
