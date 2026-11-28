namespace Andril.Economy.Wallet;

/// <summary>
/// Immutable double-entry coin transaction ledger record (CF-19).
/// Append-only record with unique idempotency key protection.
/// </summary>
public sealed class CoinTransactionRecord
{
    private CoinTransactionRecord() { }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public long Amount { get; private set; }
    public long BalanceAfter { get; private set; }
    public CoinTransactionType Type { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string? ReferenceId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static CoinTransactionRecord Create(
        Guid accountId,
        long amount,
        long balanceAfter,
        CoinTransactionType type,
        string reason,
        string idempotencyKey,
        string? referenceId,
        DateTimeOffset now)
    {
        if (accountId == Guid.Empty)
            throw new InvalidCoinOperationException("Account identifier cannot be empty.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new InvalidCoinOperationException("Idempotency key is required.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidCoinOperationException("Transaction reason is required.");
        if (balanceAfter < 0)
            throw new InvalidCoinOperationException("Balance after transaction cannot be negative.");

        return new CoinTransactionRecord
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Amount = amount,
            BalanceAfter = balanceAfter,
            Type = type,
            Reason = reason.Trim(),
            IdempotencyKey = idempotencyKey.Trim(),
            ReferenceId = referenceId?.Trim(),
            CreatedAtUtc = now.ToUniversalTime()
        };
    }
}
