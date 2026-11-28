namespace Andril.Economy.Wallet;

public interface ICoinWalletRepository
{
    Task<CoinWallet?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<CoinWallet> CreateWalletAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<CoinTransactionRecord?> FindTransactionByIdempotencyKeyAsync(Guid accountId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<CoinTransactionReceipt> ExecuteCreditAsync(Guid accountId, long amount, CoinTransactionType type, string reason, string idempotencyKey, string? referenceId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<CoinTransactionReceipt> ExecuteDebitAsync(Guid accountId, long amount, CoinTransactionType type, string reason, string idempotencyKey, string? referenceId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<CoinTransactionReceipt> ExecuteTransferAsync(Guid senderAccountId, Guid recipientAccountId, long amount, string idempotencyKey, string? note, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CoinTransactionReceipt>> GetHistoryAsync(Guid accountId, int limit, int offset, CancellationToken cancellationToken = default);
}
