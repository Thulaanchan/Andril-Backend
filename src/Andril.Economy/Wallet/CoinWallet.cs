namespace Andril.Economy.Wallet;

/// <summary>
/// Domain entity representing an account's coin wallet (CF-19).
/// Maintains the non-negative balance invariant and strict separation from Flight Points and XP.
/// </summary>
public sealed class CoinWallet
{
    private CoinWallet() { }

    public Guid AccountId { get; private set; }
    public long Balance { get; private set; }
    public uint ConcurrencyVersion { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static CoinWallet Create(Guid accountId, DateTimeOffset now)
    {
        if (accountId == Guid.Empty)
            throw new InvalidCoinOperationException("Account identifier cannot be empty.");

        var utc = now.ToUniversalTime();
        return new CoinWallet
        {
            AccountId = accountId,
            Balance = 0L,
            ConcurrencyVersion = 1,
            CreatedAtUtc = utc,
            UpdatedAtUtc = utc
        };
    }

    public void ApplyCredit(long amount, DateTimeOffset now)
    {
        if (amount <= 0)
            throw new InvalidCoinAmountException($"Credit amount must be positive. Received: {amount}");

        checked
        {
            Balance += amount;
        }

        ConcurrencyVersion++;
        UpdatedAtUtc = now.ToUniversalTime();
    }

    public void ApplyDebit(long amount, DateTimeOffset now)
    {
        if (amount <= 0)
            throw new InvalidCoinAmountException($"Debit amount must be positive. Received: {amount}");

        if (amount > Balance)
            throw new InsufficientCoinBalanceException(
                $"Insufficient coin balance. Required: {amount}, Available: {Balance}");

        Balance -= amount;
        ConcurrencyVersion++;
        UpdatedAtUtc = now.ToUniversalTime();
    }
}
