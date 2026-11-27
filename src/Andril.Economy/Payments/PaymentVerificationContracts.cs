using System.Text.Json.Serialization;

namespace Andril.Economy.Payments;

public sealed record PaymentVerificationCommand(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("purchaseToken")] string PurchaseToken,
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey
);

public sealed record PaymentVerificationResult(
    [property: JsonPropertyName("isVerified")] bool IsVerified,
    [property: JsonPropertyName("transactionId")] string? TransactionId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("verifiedAtUtc")] DateTimeOffset? VerifiedAtUtc
);

public sealed record RefundVerificationCommand(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("reason")] string Reason
);

public sealed record RefundVerificationResult(
    [property: JsonPropertyName("isReversed")] bool IsReversed,
    [property: JsonPropertyName("transactionId")] string TransactionId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("processedAtUtc")] DateTimeOffset? ProcessedAtUtc
);
