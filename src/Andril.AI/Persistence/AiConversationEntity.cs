
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Persistence;

/// <summary>
/// Proposed M1-owned persistence entity for one private AI conversation.
/// No table, migration, DbContext registration or API is created here.
/// The real repository must verify authorization inside the database
/// transaction and save the first user message atomically with this entity.
/// </summary>
public sealed class AiConversationEntity
{
    // Reserved for a future ORM materializer. Do not use as a factory.
    private AiConversationEntity() { }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid ModuleProfileId { get; private set; }
    public AiModule Module { get; private set; }
    public Guid CreateOperationId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset LastSavedUserMessageAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }

    /// <summary>
    /// Constructs a new entity only after a first user message has been
    /// validated and accepted for atomic persistence. Timestamp must be
    /// supplied by the server, not a mobile client.
    /// </summary>
    public static AiConversationEntity CreateForFirstUserMessage(
        AiAuthorizedActor actor,
        Guid createOperationId,
        DateTimeOffset serverSavedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateActor(actor);

        if (createOperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An operation ID is required.",
                nameof(createOperationId));
        }

        var savedAtUtc = NormalizeUtc(
            serverSavedAtUtc,
            nameof(serverSavedAtUtc));

        return new AiConversationEntity
        {
            Id = Guid.NewGuid(),
            AccountId = actor.UserId,
            ModuleProfileId = actor.ModuleProfileId,
            Module = actor.Module,
            CreateOperationId = createOperationId,
            CreatedAtUtc = savedAtUtc,
            LastSavedUserMessageAtUtc = savedAtUtc,
            ExpiresAtUtc =
                AiConversationExpiryPolicy.GetExpiresAtUtc(savedAtUtc)
        };
    }

    /// <summary>
    /// Checks exact actor ownership and lifecycle state.
    /// The caller must separately verify server-side authorization.
    /// </summary>
    public bool IsAccessibleBy(
        AiAuthorizedActor? actor,
        DateTimeOffset nowUtc)
    {
        if (actor is null ||
            actor.UserId == Guid.Empty ||
            actor.ModuleProfileId == Guid.Empty ||
            !Enum.IsDefined(actor.Module))
        {
            return false;
        }

        return DeletedAtUtc is null &&
               actor.UserId == AccountId &&
               actor.ModuleProfileId == ModuleProfileId &&
               actor.Module == Module &&
               NormalizeUtc(nowUtc, nameof(nowUtc)) < ExpiresAtUtc;
    }

    /// <summary>
    /// Invoke only after a new user message is saved in the same
    /// database transaction. Reading, assistant replies and
    /// idempotent retries must never invoke this method.
    /// </summary>
    public void ApplySavedUserMessage(
        DateTimeOffset serverSavedAtUtc)
    {
        var savedAtUtc = NormalizeUtc(
            serverSavedAtUtc,
            nameof(serverSavedAtUtc));

        if (DeletedAtUtc is not null ||
            savedAtUtc >= ExpiresAtUtc)
        {
            throw new InvalidOperationException(
                "AI conversation unavailable.");
        }

        if (savedAtUtc < LastSavedUserMessageAtUtc)
        {
            throw new InvalidOperationException(
                "User message timestamp cannot move backward.");
        }

        LastSavedUserMessageAtUtc = savedAtUtc;

        ExpiresAtUtc =
            AiConversationExpiryPolicy.GetExpiresAtUtc(savedAtUtc);
    }

    /// <summary>
    /// Marks the conversation inaccessible when the deletion state is
    /// persisted. Physical purge and backup cleanup are separate jobs.
    /// Repeated deletion is idempotent.
    /// </summary>
    public void MarkDeleted(
        DateTimeOffset serverDeletedAtUtc)
    {
        var deletedAtUtc = NormalizeUtc(
            serverDeletedAtUtc,
            nameof(serverDeletedAtUtc));

        if (deletedAtUtc < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(serverDeletedAtUtc));
        }

        DeletedAtUtc ??= deletedAtUtc;
    }

    private static void ValidateActor(
        AiAuthorizedActor actor)
    {
        if (actor.UserId == Guid.Empty ||
            actor.ModuleProfileId == Guid.Empty ||
            !Enum.IsDefined(actor.Module))
        {
            throw new ArgumentException(
                "A valid authorized actor is required.",
                nameof(actor));
        }
    }

    private static DateTimeOffset NormalizeUtc(
        DateTimeOffset instant,
        string name)
    {
        if (instant == default)
        {
            throw new ArgumentException(
                "A server timestamp is required.",
                name);
        }

        return instant.ToUniversalTime();
    }
}
