namespace Andril.IntegrationTests.Communication.Chat.Prototype;

// Proposed test-only HTTP shapes. Never serialize domain entities or private actor identifiers.
internal sealed record ChatPrototypeConversationDto(Guid ConversationId, string Module);
internal sealed record ChatPrototypeMessageDto(Guid MessageId, Guid ConversationId, string Module,
    string Text, DateTimeOffset CreatedAtUtc, bool IsOwnMessage);
internal sealed record ChatPrototypeSubmitDto(string? Text, string? MoodTone = null);
internal sealed record ChatPrototypePage<T>(IReadOnlyList<T> Items, string? NextCursor);
internal sealed record ChatPrototypeError(string Code, string Title, int Status);
