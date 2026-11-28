using System.Text.Json.Serialization;

namespace Andril.Economy.VirtualGifts;

public sealed record SendVirtualGiftRequest(
    [property: JsonPropertyName("recipientAccountId")] Guid RecipientAccountId,
    [property: JsonPropertyName("giftId")] string GiftId,
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("message")] string? Message
);

public sealed record GiftTransactionReceipt(
    [property: JsonPropertyName("giftTransactionId")] Guid GiftTransactionId,
    [property: JsonPropertyName("senderAccountId")] Guid SenderAccountId,
    [property: JsonPropertyName("recipientAccountId")] Guid RecipientAccountId,
    [property: JsonPropertyName("giftId")] string GiftId,
    [property: JsonPropertyName("coinCost")] long CoinCost,
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc
);
