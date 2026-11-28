using Andril.Economy.Payments;
using Andril.Economy.Premium;
using Andril.Economy.Wallet;
using Xunit;

namespace Andril.SecurityTests.Economy;

public sealed class WalletSecurityTests
{
    [Fact]
    public async Task PaymentVerification_UnconfiguredProvider_FailsClosed()
    {
        var walletStub = new StubCoinWalletService();
        var entitlementStub = new StubEntitlementService();
        var timeProvider = TimeProvider.System;

        var paymentService = new PaymentVerificationService(walletStub, entitlementStub, timeProvider);

        var command = new PaymentVerificationCommand(
            Guid.NewGuid(),
            "stripe", // Live payment provider without configured gateway credentials
            "tok_unverified_payload_abc",
            "coins_100",
            "idemp_sec_test");

        var result = await paymentService.VerifyPurchaseAsync(command);

        // Security boundary: Must fail closed
        Assert.False(result.IsVerified);
        Assert.Null(result.TransactionId);
        Assert.Contains("not configured", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentVerification_UnsupportedProvider_FailsClosed()
    {
        var walletStub = new StubCoinWalletService();
        var entitlementStub = new StubEntitlementService();

        var paymentService = new PaymentVerificationService(walletStub, entitlementStub, TimeProvider.System);

        var command = new PaymentVerificationCommand(
            Guid.NewGuid(),
            "fake_crypto_pay",
            "tok_xyz",
            "coins_500",
            "idemp_sec_test_2");

        var result = await paymentService.VerifyPurchaseAsync(command);

        Assert.False(result.IsVerified);
        Assert.Contains("Unsupported", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoinWallet_DebitBeyondBalance_FailsClosedWithoutBalanceMutation()
    {
        var wallet = CoinWallet.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        wallet.ApplyCredit(100, DateTimeOffset.UtcNow);

        // Attempting to debit 101 coins from 100 coins
        Assert.Throws<InsufficientCoinBalanceException>(() =>
            wallet.ApplyDebit(101, DateTimeOffset.UtcNow));

        // Balance must remain exactly 100
        Assert.Equal(100L, wallet.Balance);
    }

    private sealed class StubCoinWalletService : ICoinWalletService
    {
        public Task<CoinWalletDto> GetMyWalletAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoinWalletDto(Guid.NewGuid(), 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        public Task<CoinTransactionReceipt> CreditMyWalletAsync(long amount, CoinTransactionType type, string reason, string idempotencyKey, string? referenceId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoinTransactionReceipt(Guid.NewGuid(), Guid.NewGuid(), amount, amount, type, reason, idempotencyKey, referenceId, DateTimeOffset.UtcNow));

        public Task<CoinTransactionReceipt> DebitMyWalletAsync(long amount, CoinTransactionType type, string reason, string idempotencyKey, string? referenceId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoinTransactionReceipt(Guid.NewGuid(), Guid.NewGuid(), -amount, 0, type, reason, idempotencyKey, referenceId, DateTimeOffset.UtcNow));

        public Task<CoinTransactionReceipt> TransferCoinsAsync(Guid recipientAccountId, long amount, string idempotencyKey, string? note = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoinTransactionReceipt(Guid.NewGuid(), Guid.NewGuid(), -amount, 0, CoinTransactionType.TransferDebit, "Transfer", idempotencyKey, recipientAccountId.ToString(), DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<CoinTransactionReceipt>> GetMyHistoryAsync(int limit = 20, int offset = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoinTransactionReceipt>>([]);
    }

    private sealed class StubEntitlementService : IEntitlementService
    {
        public Task<UserEntitlementDto> GetMyEntitlementAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserEntitlementDto(Guid.NewGuid(), SubscriptionTier.Free, false, false, null, "none"));

        public Task<UserEntitlementDto> GetEntitlementByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserEntitlementDto(accountId, SubscriptionTier.Free, false, false, null, "none"));

        public Task<UserEntitlementDto> GrantSubscriptionAsync(Guid accountId, SubscriptionTier tier, DateTimeOffset? expiresAt, string provider, string? externalId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserEntitlementDto(accountId, tier, true, true, expiresAt, provider));

        public Task<UserEntitlementDto> RevokeSubscriptionAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserEntitlementDto(accountId, SubscriptionTier.Free, false, false, null, "none"));
    }
}
