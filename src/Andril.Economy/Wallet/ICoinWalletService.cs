namespace Andril.Economy.Wallet;

public interface ICoinWalletService
{
    Task<CoinWalletDto> GetMyWalletAsync(CancellationToken cancellationToken = default);
    Task<CoinTransactionReceipt> CreditMyWalletAsync(long amount, CoinTransactionType type, string reason, string idempotencyKey, string? referenceId = null, CancellationToken cancellationToken = default);
    Task<CoinTransactionReceipt> DebitMyWalletAsync(long amount, CoinTransactionType type, string reason, string idempotencyKey, string? referenceId = null, CancellationToken cancellationToken = default);
    Task<CoinTransactionReceipt> TransferCoinsAsync(Guid recipientAccountId, long amount, string idempotencyKey, string? note = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CoinTransactionReceipt>> GetMyHistoryAsync(int limit = 20, int offset = 0, CancellationToken cancellationToken = default);
}
