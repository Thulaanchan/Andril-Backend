using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Economy.Wallet;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Economy;

public sealed class PostgresCoinWalletRepository(EconomyDbContext db) : ICoinWalletRepository
{
    public async Task<CoinWallet?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account is required.", nameof(accountId));

        try
        {
            return await db.CoinWallets.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<CoinWallet> CreateWalletAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var wallet = CoinWallet.Create(accountId, now);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_economy.coin_wallets (account_id, balance, concurrency_version, created_at_utc, updated_at_utc)
                VALUES ({wallet.AccountId}, {wallet.Balance}, {wallet.ConcurrencyVersion}, {wallet.CreatedAtUtc}, {wallet.UpdatedAtUtc})
                ON CONFLICT (account_id) DO NOTHING
                """, cancellationToken);

            var result = await db.CoinWallets.AsNoTracking().SingleAsync(x => x.AccountId == accountId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<CoinTransactionRecord?> FindTransactionByIdempotencyKeyAsync(
        Guid accountId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.CoinTransactions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.AccountId == accountId && x.IdempotencyKey == idempotencyKey, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<CoinTransactionReceipt> ExecuteCreditAsync(
        Guid accountId,
        long amount,
        CoinTransactionType type,
        string reason,
        string idempotencyKey,
        string? referenceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            // Check existing idempotency
            var existingTx = await db.CoinTransactions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.AccountId == accountId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existingTx is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ToReceipt(existingTx);
            }

            var wallet = await db.CoinWallets.SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
            if (wallet is null)
            {
                wallet = CoinWallet.Create(accountId, now);
                db.CoinWallets.Add(wallet);
                await db.SaveChangesAsync(cancellationToken);
            }

            wallet.ApplyCredit(amount, now);

            var ledgerEntry = CoinTransactionRecord.Create(
                accountId, amount, wallet.Balance, type, reason, idempotencyKey, referenceId, now);

            db.CoinTransactions.Add(ledgerEntry);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return ToReceipt(ledgerEntry);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("uq_coin_transactions_account_idempotency") == true)
        {
            var existing = await db.CoinTransactions.AsNoTracking()
                .SingleAsync(x => x.AccountId == accountId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            return ToReceipt(existing);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<CoinTransactionReceipt> ExecuteDebitAsync(
        Guid accountId,
        long amount,
        CoinTransactionType type,
        string reason,
        string idempotencyKey,
        string? referenceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var existingTx = await db.CoinTransactions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.AccountId == accountId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existingTx is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ToReceipt(existingTx);
            }

            var wallet = await db.CoinWallets.SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken)
                         ?? throw new InsufficientCoinBalanceException("Coin wallet not found. Balance is 0.");

            wallet.ApplyDebit(amount, now);

            var ledgerEntry = CoinTransactionRecord.Create(
                accountId, -amount, wallet.Balance, type, reason, idempotencyKey, referenceId, now);

            db.CoinTransactions.Add(ledgerEntry);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return ToReceipt(ledgerEntry);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("uq_coin_transactions_account_idempotency") == true)
        {
            var existing = await db.CoinTransactions.AsNoTracking()
                .SingleAsync(x => x.AccountId == accountId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            return ToReceipt(existing);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<CoinTransactionReceipt> ExecuteTransferAsync(
        Guid senderAccountId,
        Guid recipientAccountId,
        long amount,
        string idempotencyKey,
        string? note,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var existingTx = await db.CoinTransactions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.AccountId == senderAccountId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existingTx is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return ToReceipt(existingTx);
            }

            var senderWallet = await db.CoinWallets.SingleOrDefaultAsync(x => x.AccountId == senderAccountId, cancellationToken)
                               ?? throw new InsufficientCoinBalanceException("Sender coin wallet not found. Balance is 0.");

            senderWallet.ApplyDebit(amount, now);

            var recipientWallet = await db.CoinWallets.SingleOrDefaultAsync(x => x.AccountId == recipientAccountId, cancellationToken);
            if (recipientWallet is null)
            {
                recipientWallet = CoinWallet.Create(recipientAccountId, now);
                db.CoinWallets.Add(recipientWallet);
                await db.SaveChangesAsync(cancellationToken);
            }

            recipientWallet.ApplyCredit(amount, now);

            var senderLedger = CoinTransactionRecord.Create(
                senderAccountId,
                -amount,
                senderWallet.Balance,
                CoinTransactionType.TransferDebit,
                $"Transfer to {recipientAccountId}{(string.IsNullOrWhiteSpace(note) ? string.Empty : $": {note}")}",
                idempotencyKey,
                recipientAccountId.ToString(),
                now);

            var recipientLedger = CoinTransactionRecord.Create(
                recipientAccountId,
                amount,
                recipientWallet.Balance,
                CoinTransactionType.TransferCredit,
                $"Transfer from {senderAccountId}{(string.IsNullOrWhiteSpace(note) ? string.Empty : $": {note}")}",
                $"rec_{idempotencyKey}",
                senderAccountId.ToString(),
                now);

            db.CoinTransactions.Add(senderLedger);
            db.CoinTransactions.Add(recipientLedger);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return ToReceipt(senderLedger);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("uq_coin_transactions_account_idempotency") == true)
        {
            var existing = await db.CoinTransactions.AsNoTracking()
                .SingleAsync(x => x.AccountId == senderAccountId && x.IdempotencyKey == idempotencyKey, cancellationToken);
            return ToReceipt(existing);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<IReadOnlyList<CoinTransactionReceipt>> GetHistoryAsync(
        Guid accountId, int limit, int offset, CancellationToken cancellationToken = default)
    {
        try
        {
            var records = await db.CoinTransactions.AsNoTracking()
                .Where(x => x.AccountId == accountId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Skip(offset)
                .Take(limit)
                .ToListAsync(cancellationToken);

            return records.Select(ToReceipt).ToList();
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    private static CoinTransactionReceipt ToReceipt(CoinTransactionRecord r) =>
        new(r.Id, r.AccountId, r.Amount, r.BalanceAfter, r.Type, r.Reason, r.IdempotencyKey, r.ReferenceId, r.CreatedAtUtc);
}
