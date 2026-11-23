using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;

namespace Andril.IntegrationTests.Communication.Chat;

/// <summary>
/// In-memory request/transaction-slot simulation only. Delegates every operation to the existing
/// authorizer; implements no M2/M5 eligibility rules, SQL, sessions, pooling or runtime authorization.
/// Object identities are local simulation markers, not request/revocation token formats.
/// PostgreSQL context propagation, reset and concurrency still require M2/M6 design and live tests.
/// </summary>
internal sealed class ChatActorContextIsolationContract(IChatAccessAuthorizer? authorizer, ICurrentUser? user)
{
    private readonly object sync = new();
    private object frame = new();
    private object operation = new();
    private object? request;
    private object? transaction;
    private ChatAuthorizedActor? actor;
    private CancellationToken actorCancellation;

    internal bool BeginSimulatedTransaction(object? requestIdentity, object? transactionIdentity)
    {
        lock (sync)
        {
            Clear();
            if (requestIdentity is null || transactionIdentity is null)
                return false;
            request = requestIdentity;
            transaction = transactionIdentity;
            return true;
        }
    }

    internal async Task<bool> BindOperationAsync(object requestIdentity, object transactionIdentity,
        ChatModule module, Guid conversationId, CancellationToken cancellationToken = default)
    {
        object capturedFrame;
        object capturedOperation;
        Guid subject;
        lock (sync)
        {
            actor = null;
            operation = new object();
            capturedFrame = frame;
            capturedOperation = operation;
            if (request is null || transaction is null || !ReferenceEquals(request, requestIdentity)
                || !ReferenceEquals(transaction, transactionIdentity) || !Enum.IsDefined(module)
                || conversationId == Guid.Empty || authorizer is null || user is null
                || !user.IsAuthenticated || user.UserId is not Guid id || id == Guid.Empty)
                return false;
            subject = id;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = await authorizer!.AuthorizeConversationAsync(module, conversationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!ReferenceEquals(frame, capturedFrame) || !ReferenceEquals(operation, capturedOperation))
                    return false;
                if (resolved is null || user is null || !user.IsAuthenticated || user.UserId != subject
                    || resolved.VerifiedSubjectId != subject || resolved.AccountId == Guid.Empty
                    || resolved.ModuleProfileId == Guid.Empty || resolved.Module != module || resolved.ConversationId != conversationId)
                    return false;
                actor = resolved;
                actorCancellation = cancellationToken;
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearIfCurrent(capturedFrame, capturedOperation);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            ClearIfCurrent(capturedFrame, capturedOperation);
            return false;
        }
    }

    /// <summary>Observes simulated state only. This is never permission to perform a database operation.</summary>
    internal bool HasSimulatedContext(object requestIdentity, object transactionIdentity, ChatModule module, Guid conversationId)
    {
        lock (sync)
        {
            if (actor is not null && (actorCancellation.IsCancellationRequested || user?.IsAuthenticated != true
                || user?.UserId != actor.VerifiedSubjectId))
                Clear();
            return actor is not null && ReferenceEquals(request, requestIdentity) && ReferenceEquals(transaction, transactionIdentity)
                && actor.Module == module && actor.ConversationId == conversationId;
        }
    }

    internal void EndSimulatedTransaction()
    {
        lock (sync)
            Clear();
    }

    internal bool MayUseDatabase => false;
    public override string ToString() => nameof(ChatActorContextIsolationContract);

    private void ClearIfCurrent(object capturedFrame, object capturedOperation)
    {
        lock (sync)
            if (ReferenceEquals(frame, capturedFrame) && ReferenceEquals(operation, capturedOperation))
                Clear();
    }

    private void Clear()
    {
        frame = new object();
        operation = new object();
        request = null;
        transaction = null;
        actor = null;
        actorCancellation = default;
    }
}
