using Andril.Economy.Wallet;
using Xunit;

namespace Andril.UnitTests.Economy;

public sealed class CoinWalletTests
{
    private readonly DateTimeOffset _testTime = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_NewWallet_HasZeroBalanceAndInitialVersion()
    {
        var accountId = Guid.NewGuid();
        var wallet = CoinWallet.Create(accountId, _testTime);

        Assert.Equal(accountId, wallet.AccountId);
        Assert.Equal(0L, wallet.Balance);
        Assert.Equal(1u, wallet.ConcurrencyVersion);
        Assert.Equal(_testTime, wallet.CreatedAtUtc);
        Assert.Equal(_testTime, wallet.UpdatedAtUtc);
    }

    [Fact]
    public void Create_EmptyAccountId_ThrowsException()
    {
        Assert.Throws<InvalidCoinOperationException>(() =>
            CoinWallet.Create(Guid.Empty, _testTime));
    }

    [Fact]
    public void ApplyCredit_PositiveAmount_IncreasesBalanceAndVersion()
    {
        var wallet = CoinWallet.Create(Guid.NewGuid(), _testTime);

        var creditTime = _testTime.AddMinutes(5);
        wallet.ApplyCredit(250, creditTime);

        Assert.Equal(250L, wallet.Balance);
        Assert.Equal(2u, wallet.ConcurrencyVersion);
        Assert.Equal(creditTime, wallet.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void ApplyCredit_NonPositiveAmount_ThrowsInvalidCoinAmountException(long invalidAmount)
    {
        var wallet = CoinWallet.Create(Guid.NewGuid(), _testTime);

        Assert.Throws<InvalidCoinAmountException>(() =>
            wallet.ApplyCredit(invalidAmount, _testTime));
    }

    [Fact]
    public void ApplyDebit_SufficientBalance_DecreasesBalance()
    {
        var wallet = CoinWallet.Create(Guid.NewGuid(), _testTime);
        wallet.ApplyCredit(500, _testTime);

        var debitTime = _testTime.AddMinutes(10);
        wallet.ApplyDebit(200, debitTime);

        Assert.Equal(300L, wallet.Balance);
        Assert.Equal(3u, wallet.ConcurrencyVersion);
        Assert.Equal(debitTime, wallet.UpdatedAtUtc);
    }

    [Fact]
    public void ApplyDebit_InsufficientBalance_ThrowsInsufficientCoinBalanceException()
    {
        var wallet = CoinWallet.Create(Guid.NewGuid(), _testTime);
        wallet.ApplyCredit(50, _testTime);

        Assert.Throws<InsufficientCoinBalanceException>(() =>
            wallet.ApplyDebit(100, _testTime));
        Assert.Equal(50L, wallet.Balance); // Balance must remain unchanged
    }

    [Fact]
    public void TransactionRecord_ValidCreation_StoresExactFields()
    {
        var accountId = Guid.NewGuid();
        var record = CoinTransactionRecord.Create(
            accountId,
            500,
            500,
            CoinTransactionType.PurchaseCredit,
            "Initial coin pack purchase",
            "idemp_test_123",
            "ref_pay_abc",
            _testTime);

        Assert.NotEqual(Guid.Empty, record.Id);
        Assert.Equal(accountId, record.AccountId);
        Assert.Equal(500L, record.Amount);
        Assert.Equal(500L, record.BalanceAfter);
        Assert.Equal(CoinTransactionType.PurchaseCredit, record.Type);
        Assert.Equal("idemp_test_123", record.IdempotencyKey);
        Assert.Equal("ref_pay_abc", record.ReferenceId);
    }

    [Fact]
    public void TransactionRecord_NegativeBalanceAfter_ThrowsException()
    {
        Assert.Throws<InvalidCoinOperationException>(() =>
            CoinTransactionRecord.Create(
                Guid.NewGuid(),
                -100,
                -10, // Invalid: balance cannot be negative
                CoinTransactionType.TransferDebit,
                "Invalid debit",
                "idemp_fail",
                null,
                _testTime));
    }
}
