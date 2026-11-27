using Andril.Shared.Authentication;

namespace Andril.Economy.Wallet;

public sealed class CoinWalletService(
    ICurrentUser currentUser,
    ICoinWalletRepository walletRepository,
    TimeProvider clock)
    : ICoinWalletService
{
    public async Task<CoinWalletDto> GetMyWalletAsync(CancellationToken cancellationToken = default)
    {
        var accountId = RequireAuthenticatedAccountId();
        var wallet = await walletRepository.GetByAccountIdAsync(accountId, cancellationToken);
        if (wallet is null)
        {
            wallet = await walletRepository.CreateWalletAsync(accountId, clock.GetUtcNow(), cancellationToken);
        }

        return ToDto(wallet);
    }

    public async Task<CoinTransactionReceipt> CreditMyWalletAsync(
        long amount,
        CoinTransactionType type,
        string reason,
        string idempotencyKey,
        string? referenceId = null,
        CancellationToken cancellationToken = default)
    {
        var accountId = RequireAuthenticatedAccountId();
        ValidateTransactionParams(amount, idempotencyKey, reason);

        return await walletRepository.ExecuteCreditAsync(
            accountId, amount, type, reason, idempotencyKey, referenceId, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<CoinTransactionReceipt> DebitMyWalletAsync(
        long amount,
        CoinTransactionType type,
        string reason,
        string idempotencyKey,
        string? referenceId = null,
        CancellationToken cancellationToken = default)
    {
        var accountId = RequireAuthenticatedAccountId();
        ValidateTransactionParams(amount, idempotencyKey, reason);

        return await walletRepository.ExecuteDebitAsync(
            accountId, amount, type, reason, idempotencyKey, referenceId, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<CoinTransactionReceipt> TransferCoinsAsync(
        Guid recipientAccountId,
        long amount,
        string idempotencyKey,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var senderAccountId = RequireAuthenticatedAccountId();
        if (recipientAccountId == Guid.Empty)
            throw new InvalidCoinOperationException("Recipient account identifier cannot be empty.");
        if (senderAccountId == recipientAccountId)
            throw new InvalidCoinOperationException("Self-transfers are not allowed.");

        ValidateTransactionParams(amount, idempotencyKey, "Peer-to-peer coin transfer");

        return await walletRepository.ExecuteTransferAsync(
            senderAccountId, recipientAccountId, amount, idempotencyKey, note, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<IReadOnlyList<CoinTransactionReceipt>> GetMyHistoryAsync(
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var accountId = RequireAuthenticatedAccountId();
        if (limit <= 0) limit = 20;
        if (limit > 100) limit = 100;
        if (offset < 0) offset = 0;

        return await walletRepository.GetHistoryAsync(accountId, limit, offset, cancellationToken);
    }

    private Guid RequireAuthenticatedAccountId()
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid userId || userId == Guid.Empty)
            throw new InvalidCoinOperationException("Authenticated user identity required.");
        return userId;
    }

    private static void ValidateTransactionParams(long amount, string idempotencyKey, string reason)
    {
        if (amount <= 0)
            throw new InvalidCoinAmountException($"Transaction amount must be strictly positive (> 0). Received: {amount}");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new InvalidCoinOperationException("Idempotency key is required.");
        if (idempotencyKey.Length > 128)
            throw new InvalidCoinOperationException("Idempotency key exceeds maximum length of 128 characters.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidCoinOperationException("Transaction reason is required.");
    }

    private static CoinWalletDto ToDto(CoinWallet w) =>
        new(w.AccountId, w.Balance, w.ConcurrencyVersion, w.CreatedAtUtc, w.UpdatedAtUtc);
}
