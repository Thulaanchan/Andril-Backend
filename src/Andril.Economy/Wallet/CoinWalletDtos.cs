using System.Text.Json.Serialization;

namespace Andril.Economy.Wallet;

public sealed record CoinWalletDto(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("balance")] long Balance,
    [property: JsonPropertyName("concurrencyVersion")] uint ConcurrencyVersion,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc
);

public sealed record CoinTransactionReceipt(
    [property: JsonPropertyName("transactionId")] Guid TransactionId,
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("balanceAfter")] long BalanceAfter,
    [property: JsonPropertyName("type")] CoinTransactionType Type,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("referenceId")] string? ReferenceId,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc
);

public sealed record CoinTransferRequest(
    [property: JsonPropertyName("recipientAccountId")] Guid RecipientAccountId,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("note")] string? Note
);

public sealed record CoinPurchaseCreditRequest(
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("paymentReference")] string PaymentReference
);
