
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.Interfaces;

namespace Andril.API.Integrations.AI;

/// <summary>
/// Fail-closed placeholder for AI-PRIV-02 until authoritative
/// module-scoped privacy evidence and applicable policy are verified.
/// Never authorizes AI processing or records acknowledgement or consent.
/// Do not expose the AI HTTP endpoint while this placeholder is in use.
/// </summary>
public sealed class DenyAllAiPrivacyNoticeGate : IAiPrivacyNoticeGate
{
    public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(
        AiAuthorizedActor actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Structural validation is not authentication or eligibility.
        if (actor is null ||
            actor.UserId == Guid.Empty ||
            actor.ModuleProfileId == Guid.Empty ||
            !Enum.IsDefined(actor.Module))
        {
            return Task.FromResult(AiPrivacyNoticeGateOutcome.Denied);
        }

        // No trusted privacy policy or evidence reader is available
        // in this placeholder. Never return Satisfied, infer consent,
        // or substitute client flags for authoritative evidence.
        return Task.FromResult(
            AiPrivacyNoticeGateOutcome.PolicyNotVerified);
    }
}
