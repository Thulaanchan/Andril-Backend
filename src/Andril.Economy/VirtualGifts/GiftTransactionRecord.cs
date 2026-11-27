namespace Andril.Economy.VirtualGifts;

/// <summary>
/// Immutable ledger record of a virtual gift transaction (CF-14).
/// </summary>
public sealed class GiftTransactionRecord
{
    private GiftTransactionRecord() { }

    public Guid Id { get; private set; }
    public Guid SenderAccountId { get; private set; }
    public Guid RecipientAccountId { get; private set; }
    public string GiftId { get; private set; } = string.Empty;
    public long CoinCost { get; private set; }
    public string Module { get; private set; } = string.Empty;
    public string? Message { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static GiftTransactionRecord Create(
        Guid senderAccountId,
        Guid recipientAccountId,
        string giftId,
        long coinCost,
        string module,
        string idempotencyKey,
        string? message,
        DateTimeOffset now)
    {
        if (senderAccountId == Guid.Empty)
            throw new InvalidGiftOperationException("Sender account identifier cannot be empty.");
        if (recipientAccountId == Guid.Empty)
            throw new InvalidGiftOperationException("Recipient account identifier cannot be empty.");
        if (senderAccountId == recipientAccountId)
            throw new InvalidGiftOperationException("Sending gifts to oneself is not permitted.");
        if (string.IsNullOrWhiteSpace(giftId))
            throw new InvalidGiftOperationException("Gift identifier is required.");
        if (coinCost <= 0)
            throw new InvalidGiftOperationException("Coin cost must be positive.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new InvalidGiftOperationException("Idempotency key is required.");

        return new GiftTransactionRecord
        {
            Id = Guid.NewGuid(),
            SenderAccountId = senderAccountId,
            RecipientAccountId = recipientAccountId,
            GiftId = giftId.Trim().ToLowerInvariant(),
            CoinCost = coinCost,
            Module = module?.Trim().ToLowerInvariant() ?? "social",
            Message = message?.Trim(),
            IdempotencyKey = idempotencyKey.Trim(),
            CreatedAtUtc = now.ToUniversalTime()
        };
    }
}

public sealed class InvalidGiftOperationException : Exception
{
    public InvalidGiftOperationException(string message) : base(message) { }
}
