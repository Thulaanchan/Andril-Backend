using System.Diagnostics;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;

namespace Andril.Communication.Chat.Development;

/// <summary>
/// Internal development conversation operations over existing domain entities.
/// Production D3/D6 authorizers remain non-granting. Success is executable only
/// with explicit isolated test authorities; this service is never registered.
/// Rechecks here do not establish distributed revocation or persistence atomicity.
/// </summary>
internal sealed class ChatConversationManagementService(
    ICurrentUser user,
    IChatAccessAuthorizer authorizer,
    IChatConversationRepository repository,
    IChatConversationDevelopmentAuthority? authority = null)
{
    private static readonly ActivitySource Activities = new("Andril.Communication.Chat.Conversations");

    internal Task<ChatConversationResult<ChatConversation>> OpenAsync(
        ChatModule module, Guid conversationId, CancellationToken cancellationToken = default) =>
        GetDetailsAsync(module, conversationId, cancellationToken);

    internal Task<ChatConversationResult<ChatConversation>> GetDetailsAsync(
        ChatModule module, Guid conversationId, CancellationToken cancellationToken = default) =>
        WithConversationAsync(module, conversationId, ChatOperation.ReadConversationDetails,
            (_, conversation, _) => Task.FromResult(new ChatConversationResult<ChatConversation>(
                ChatConversationStatus.Success, conversation)), cancellationToken);

    // Shared protected boundary for D8 reads and D9 messages. The requested
    // permission is checked independently before access and before local delivery.
    internal Task<ChatConversationResult<T>> WithConversationAsync<T>(
        ChatModule module, Guid conversationId, ChatOperation operation,
        Func<ChatConversationScope, ChatConversation, CancellationToken, Task<ChatConversationResult<T>>> access,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<T>(module, operation.ToString(), async scope =>
        {
            if (conversationId == Guid.Empty || operation is not (ChatOperation.ReadConversationDetails
                or ChatOperation.ReadMessageHistory or ChatOperation.SubmitMessage))
                return new(ChatConversationStatus.InvalidRequest);
            var request = new ChatAuthorizationRequest(operation, module, conversationId);
            var permission = await CheckAsync(scope, request, cancellationToken);
            if (permission != ChatConversationStatus.Success) return new(permission);

            var conversation = await repository.FindAsync(module, conversationId, cancellationToken);
            if (conversation is null || conversation.ConversationId != conversationId ||
                !DevelopmentChatConversationRepository.Contains(conversation, scope.Participant))
                return new(ChatConversationStatus.Unavailable);

            // Lookup may await external work. Observe revocation again before a
            // message callback can mutate local storage, then recheck delivery.
            permission = await CheckAsync(scope, request, cancellationToken);
            if (permission != ChatConversationStatus.Success) return new(permission);
            var result = await access(scope, conversation, cancellationToken);
            permission = await CheckAsync(scope, request, cancellationToken);
            return permission == ChatConversationStatus.Success ? result : new(permission);
        }, cancellationToken);

    internal Task<ChatConversationResult<IReadOnlyList<ChatConversation>>> ListAsync(
        ChatModule module, CancellationToken cancellationToken = default) =>
        ExecuteAsync<IReadOnlyList<ChatConversation>>(module, "list", async scope =>
        {
            var request = new ChatAuthorizationRequest(ChatOperation.ListConversations, module, null);
            var permission = await CheckAsync(scope, request, cancellationToken);
            if (permission != ChatConversationStatus.Success) return new(permission);
            var candidates = await repository.ListAsync(scope.Participant, cancellationToken);
            var visible = new List<ChatConversation>();
            foreach (var conversation in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!DevelopmentChatConversationRepository.Contains(conversation, scope.Participant))
                    return new(ChatConversationStatus.Unavailable);
                var item = await authority!.AuthorizeListingItemAsync(scope, conversation, cancellationToken);
                if (item == ChatAuthorizationOutcome.Unavailable || !Enum.IsDefined(item))
                    return new(ChatConversationStatus.Unavailable);
                if (item == ChatAuthorizationOutcome.Allowed) visible.Add(conversation);
            }

            // Revalidate every selected item at the local projection boundary.
            // This finite development list has no pagination/count side channel.
            foreach (var conversation in visible)
                if (await authority!.AuthorizeListingItemAsync(scope, conversation, cancellationToken) != ChatAuthorizationOutcome.Allowed)
                    return new(ChatConversationStatus.Unavailable);

            permission = await CheckAsync(scope, request, cancellationToken);
            return permission == ChatConversationStatus.Success
                ? new(ChatConversationStatus.Success, visible.AsReadOnly())
                : new(permission);
        }, cancellationToken);

    internal Task<ChatConversationResult<ChatConversation>> CreateAsync(
        ChatModule module, Guid requestId, ChatConversation candidate, CancellationToken cancellationToken = default) =>
        ExecuteAsync<ChatConversation>(module, "create", async scope =>
        {
            if (requestId == Guid.Empty || candidate is null ||
                !DevelopmentChatConversationRepository.Contains(candidate, scope.Participant))
                return new(ChatConversationStatus.InvalidRequest);

            // Creation has no D3 operation mapping. It requires its own explicit
            // development authority; details/history/submission never imply creation.
            var permission = Outcome(await authority!.AuthorizeCreationAsync(scope, candidate, requestId, cancellationToken));
            if (permission != ChatConversationStatus.Success) return new(permission);
            if (!SameScope(scope, await ReadScopeAsync(module, cancellationToken)))
                return new(ChatConversationStatus.Unavailable);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await repository.GetOrCreateAsync(scope, requestId, candidate, cancellationToken);
            if (result.Status != ChatConversationStatus.Success)
                return new(Enum.IsDefined(result.Status) ? result.Status : ChatConversationStatus.Unavailable);
            if (result.Value is null || !DevelopmentChatConversationRepository.Equivalent(candidate, result.Value))
                return new(ChatConversationStatus.Unavailable);

            permission = Outcome(await authority!.AuthorizeCreationAsync(scope, candidate, requestId, cancellationToken));
            if (permission != ChatConversationStatus.Success) return new(permission);
            return SameScope(scope, await ReadScopeAsync(module, cancellationToken))
                ? result
                : new(ChatConversationStatus.Unavailable);
        }, cancellationToken);

    private async Task<ChatConversationResult<T>> ExecuteAsync<T>(
        ChatModule module, string operation, Func<ChatConversationScope, Task<ChatConversationResult<T>>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = Activities.StartActivity(operation);
        try
        {
            if (!Enum.IsDefined(module)) return new(ChatConversationStatus.InvalidRequest);
            var scope = await ReadScopeAsync(module, cancellationToken);
            if (scope is null) return new(ChatConversationStatus.Unavailable);
            var result = await action(scope);
            cancellationToken.ThrowIfCancellationRequested();
            // Only operation and outcome are diagnostic tags. No identities/content.
            activity?.SetTag("chat.outcome", result.Status.ToString());
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity?.SetTag("chat.outcome", nameof(ChatConversationStatus.Unavailable));
            return new(ChatConversationStatus.Unavailable);
        }
    }

    private async Task<ChatConversationScope?> ReadScopeAsync(ChatModule module, CancellationToken cancellationToken)
    {
        if (authority is null || !user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            return null;
        var scope = await authority.ReadScopeAsync(subject, module, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return user.IsAuthenticated && user.UserId == subject &&
            scope?.VerifiedSubjectId == subject && scope.Participant.Module == module
            ? scope : null;
    }

    private async Task<ChatConversationStatus> CheckAsync(
        ChatConversationScope scope, ChatAuthorizationRequest request, CancellationToken cancellationToken)
    {
        var outcome = await authorizer.AuthorizeAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!SameScope(scope, await ReadScopeAsync(request.Module, cancellationToken)))
            return ChatConversationStatus.Unavailable;
        return Outcome(outcome);
    }

    private static bool SameScope(ChatConversationScope left, ChatConversationScope? right) =>
        right is not null && left.VerifiedSubjectId == right.VerifiedSubjectId &&
        left.Participant.Module == right.Participant.Module &&
        left.Participant.AccountId == right.Participant.AccountId &&
        left.Participant.ModuleProfileId == right.Participant.ModuleProfileId;

    private static ChatConversationStatus Outcome(ChatAuthorizationOutcome outcome) => outcome switch
    {
        ChatAuthorizationOutcome.Allowed => ChatConversationStatus.Success,
        ChatAuthorizationOutcome.Denied => ChatConversationStatus.Denied,
        _ => ChatConversationStatus.Unavailable
    };
}
