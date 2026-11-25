using System.Runtime.CompilerServices;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;

// Keep the proposed policy/evidence seam internal; synthetic providers live only
// in the unit-test assembly, with no production registration or public flag API.
[assembly: InternalsVisibleTo("Andril.UnitTests")]

namespace Andril.Communication.Chat;

/// <summary>
/// Internal policy foundation only. Not registered in production and does not
/// replace DenyAllChatAccessAuthorizer. The reader contract still requires M2/M5
/// review and authoritative implementation before any production use.
/// </summary>
internal sealed class ChatConversationAccessPolicy : IChatAccessAuthorizer
{
    private readonly ICurrentUser? _currentUser;
    private readonly IChatConversationAccessEvidenceReader? _reader;
    private readonly TimeProvider _clock;

    internal ChatConversationAccessPolicy(
        ICurrentUser? currentUser,
        IChatConversationAccessEvidenceReader? reader,
        TimeProvider? clock = null)
    {
        _currentUser = currentUser;
        _reader = reader;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ChatAuthorizationOutcome> AuthorizeAsync(
        ChatAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || !Enum.IsDefined(request.Operation) || !Enum.IsDefined(request.Module) ||
            (request.Operation == ChatOperation.ListConversations
                ? request.ConversationId is not null
                : request.ConversationId is null || request.ConversationId == Guid.Empty))
        {
            return ChatAuthorizationOutcome.Denied;
        }

        // M5 listing scope is pending. A profile or conversation cannot supply that scope.
        if (request.Operation == ChatOperation.ListConversations || _currentUser is null || _reader is null)
            return ChatAuthorizationOutcome.Unavailable;

        try
        {
            if (!_currentUser.IsAuthenticated || _currentUser.UserId is not Guid subject || subject == Guid.Empty)
                return ChatAuthorizationOutcome.Unavailable;

            var evaluationId = Guid.NewGuid();
            var startedAt = _clock.GetUtcNow();
            var evidence = await _reader.ReadOperationAsync(subject, request, evaluationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var now = _clock.GetUtcNow();
            if (!_currentUser.IsAuthenticated || _currentUser.UserId != subject || evidence is null)
                return ChatAuthorizationOutcome.Unavailable;

            var binding = evidence.Binding;
            if (evidence.EvaluationId != evaluationId || evidence.Request != request || binding is null ||
                binding.RequestId != evaluationId || binding.AccountSubjectId != subject ||
                binding.AccountId == Guid.Empty || binding.ProfileAccountId != binding.AccountId ||
                binding.ModuleProfileId == Guid.Empty || binding.ProfileModule != request.Module ||
                binding.Conversation is not { } conversation || conversation.Module != request.Module ||
                conversation.ConversationId != request.ConversationId ||
                !conversation.Participants.Any(p => p.Module == request.Module &&
                    p.AccountId == binding.AccountId && p.ModuleProfileId == binding.ModuleProfileId) ||
                binding.IsCurrent is not true || binding.ObservedAtUtc < startedAt ||
                binding.ObservedAtUtc > now || binding.ValidUntilUtc <= now)
                return ChatAuthorizationOutcome.Unavailable;

            if (binding.IsRevoked is true || binding.IsBlocked is true ||
                evidence.Outcome == ChatAuthorizationOutcome.Denied)
                return ChatAuthorizationOutcome.Denied;

            // Positive operation permissions and M2 Chat-purpose evidence await owner ratification.
            // Even a synthetic Allowed envelope cannot create a grant in this phase.
            return ChatAuthorizationOutcome.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ChatAuthorizationOutcome.Unavailable;
        }
    }

    public async Task<ChatAuthorizedActor?> AuthorizeConversationAsync(
        ChatModule module,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Enum.IsDefined(module) || conversationId == Guid.Empty || _currentUser is null || _reader is null)
        {
            return null;
        }

        try
        {
            if (!_currentUser.IsAuthenticated || _currentUser.UserId is not Guid subjectId || subjectId == Guid.Empty)
            {
                return null;
            }

            var requestId = Guid.NewGuid();
            var requestedAt = _clock.GetUtcNow();
            var evidence = await _reader.ReadAsync(subjectId, module, conversationId, requestId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var now = _clock.GetUtcNow();

            if (evidence is null || evidence.RequestId != requestId ||
                evidence.AccountSubjectId != subjectId || evidence.AccountId == Guid.Empty ||
                evidence.ProfileAccountId != evidence.AccountId || evidence.ModuleProfileId == Guid.Empty ||
                evidence.ProfileModule != module || evidence.IsCurrent is not true ||
                evidence.ObservedAtUtc < requestedAt || evidence.ObservedAtUtc > now || evidence.ValidUntilUtc <= now ||
                evidence.IsProfileActive is not true || evidence.IsProfileEligible is not true ||
                evidence.IsParticipationAuthorized is not true || evidence.IsModuleCommunicationEligible is not true ||
                evidence.IsBlocked is not false || evidence.IsRevoked is not false)
            {
                return null;
            }

            var conversation = evidence.Conversation;
            if (conversation is null || conversation.Module != module || conversation.ConversationId != conversationId ||
                !conversation.Participants.Any(participant => participant.Module == module &&
                    participant.AccountId == evidence.AccountId && participant.ModuleProfileId == evidence.ModuleProfileId))
            {
                return null;
            }

            if (module == ChatModule.Social &&
                (evidence.IsAdult is not true || evidence.HasSocialOptIn is not true || evidence.HasMutualMatch is not true))
            {
                return null;
            }

            // The trusted request context may have been revoked/changed while
            // dependencies were being read. Never reuse an earlier actor result.
            if (!_currentUser.IsAuthenticated || _currentUser.UserId != subjectId)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ChatAuthorizedActor(subjectId, evidence.AccountId, evidence.ModuleProfileId, module, conversationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Preserve caller cancellation without exposing a reader's message.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // Do not disclose dependency exceptions, identities or existence.
            // A dependency's own timeout/cancellation is unavailable evidence.
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
