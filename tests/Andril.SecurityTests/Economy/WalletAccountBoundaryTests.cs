using Andril.API.Authorization.Economy;
using Andril.API.Extensions;
using Andril.Common.Accounts;
using Andril.Economy.Wallet;
using Andril.Shared.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Economy;

public sealed class WalletAccountBoundaryTests
{
    private static readonly Guid Subject = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Recipient = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("read")]
    [InlineData("credit")]
    [InlineData("debit")]
    [InlineData("transfer")]
    [InlineData("history")]
    public async Task EveryOperationUsesInternalAccountInsteadOfAuthenticationSubject(string operation)
    {
        var account = Account.Create(Subject, Now);
        var accounts = new Accounts { Account = account };
        var ledger = new Ledger();
        using var cancellation = new CancellationTokenSource();

        await Invoke(Create(new User(), accounts, ledger), operation, cancellation.Token);

        Assert.Equal(account.Id, ledger.AccountId);
        Assert.NotEqual(Subject, ledger.AccountId);
        Assert.Equal(Subject, accounts.Subject);
        Assert.Equal(cancellation.Token, accounts.Token);
        Assert.Equal(cancellation.Token, ledger.Token);
        Assert.Equal(operation == "read" ? 2 : 1, ledger.Calls);
        if (operation == "transfer") Assert.Equal(Recipient, ledger.RecipientId);
    }

    [Theory]
    [InlineData("read", false)]
    [InlineData("credit", false)]
    [InlineData("debit", false)]
    [InlineData("transfer", false)]
    [InlineData("history", false)]
    [InlineData("read", true)]
    [InlineData("credit", true)]
    [InlineData("debit", true)]
    [InlineData("transfer", true)]
    [InlineData("history", true)]
    public async Task MissingOrForeignAccountCannotReachLedger(string operation, bool foreign)
    {
        var accounts = new Accounts { Account = foreign ? Account.Create(Recipient, Now) : null };
        var ledger = new Ledger();

        if (foreign)
            await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => Invoke(Create(new User(), accounts, ledger), operation));
        else
            await Assert.ThrowsAsync<InvalidCoinOperationException>(() => Invoke(Create(new User(), accounts, ledger), operation));

        Assert.Equal(0, ledger.Calls);
        Assert.Equal(1, accounts.Calls);
    }

    [Theory]
    [InlineData(false, "00000000-0000-0000-0000-000000000001")]
    [InlineData(true, null)]
    [InlineData(true, "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidIdentityCannotLookupOrCreateWallet(bool authenticated, string? id)
    {
        var accounts = new Accounts();
        var ledger = new Ledger();
        var user = new User { IsAuthenticated = authenticated, UserId = id is null ? null : Guid.Parse(id) };

        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => Create(user, accounts, ledger).GetMyWalletAsync());

        Assert.Equal(0, accounts.Calls);
        Assert.Equal(0, ledger.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityChangeDuringLookupCannotReachLedger(bool revoke)
    {
        var user = new User();
        var accounts = new Accounts { Account = Account.Create(Subject, Now), AfterLookup = () =>
        {
            if (revoke) user.IsAuthenticated = false;
            else user.UserId = Recipient;
        } };
        var ledger = new Ledger();

        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => Create(user, accounts, ledger).GetMyWalletAsync());

        Assert.Equal(0, ledger.Calls);
    }

    [Fact]
    public async Task CancellationDuringLookupCannotReachLedger()
    {
        using var cancellation = new CancellationTokenSource();
        var accounts = new Accounts { Account = Account.Create(Subject, Now), AfterLookup = cancellation.Cancel };
        var ledger = new Ledger();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(new User(), accounts, ledger).GetMyWalletAsync(cancellation.Token));

        Assert.Equal(0, ledger.Calls);
    }

    [Fact]
    public async Task AccountStoreFailureCannotReachLedger()
    {
        var accounts = new Accounts { Failure = new AccountStoreUnavailableException() };
        var ledger = new Ledger();

        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => Create(new User(), accounts, ledger).GetMyWalletAsync());

        Assert.Equal(0, ledger.Calls);
    }

    [Fact]
    public async Task TransferToOwnInternalAccountIsDeniedBeforeLedgerAccess()
    {
        var account = Account.Create(Subject, Now);
        var accounts = new Accounts { Account = account };
        var ledger = new Ledger();

        await Assert.ThrowsAsync<InvalidCoinOperationException>(() =>
            Create(new User(), accounts, ledger).TransferCoinsAsync(account.Id, 10, "r10-self"));

        Assert.Equal(0, ledger.Calls);
    }

    [Fact]
    public async Task ConfiguredEconomyResolvesWalletThroughAccountBoundary()
    {
        var accounts = new Accounts { Account = Account.Create(Subject, Now) };
        var ledger = new Ledger();
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new User());
        services.AddSingleton<IAccountRepository>(accounts);
        services.AddScoped<IAccountService, AccountService>();
        services.AddEconomy(new ConfigurationBuilder().Build());
        services.AddSingleton<ICoinWalletRepository>(ledger);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var wallet = await scope.ServiceProvider.GetRequiredService<ICoinWalletService>().GetMyWalletAsync();

        Assert.Equal(accounts.Account.Id, wallet.AccountId);
    }

    private static CoinWalletService Create(User user, Accounts accounts, Ledger ledger) =>
        new(new AccountBackedWalletAccountResolver(new AccountService(user, accounts, TimeProvider.System)), ledger, TimeProvider.System);

    private static async Task Invoke(ICoinWalletService service, string operation, CancellationToken token = default)
    {
        switch (operation)
        {
            case "read": await service.GetMyWalletAsync(token); break;
            case "credit": await service.CreditMyWalletAsync(10, CoinTransactionType.PurchaseCredit, "test", "r10-credit", cancellationToken: token); break;
            case "debit": await service.DebitMyWalletAsync(10, CoinTransactionType.TransferDebit, "test", "r10-debit", cancellationToken: token); break;
            case "transfer": await service.TransferCoinsAsync(Recipient, 10, "r10-transfer", cancellationToken: token); break;
            case "history": await service.GetMyHistoryAsync(cancellationToken: token); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class User : ICurrentUser
    {
        public Guid? UserId { get; set; } = Subject;
        public bool IsAuthenticated { get; set; } = true;
    }

    private sealed class Accounts : IAccountRepository
    {
        public Account? Account { get; init; }
        public Action? AfterLookup { get; init; }
        public Exception? Failure { get; init; }
        public Guid Subject { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Calls++;
            Subject = subject;
            Token = cancellationToken;
            if (Failure is not null) throw Failure;
            AfterLookup?.Invoke();
            return Task.FromResult(Account);
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Wallet access must never initialize accounts.");
    }

    private sealed class Ledger : ICoinWalletRepository
    {
        public int Calls { get; private set; }
        public Guid AccountId { get; private set; }
        public Guid RecipientId { get; private set; }
        public CancellationToken Token { get; private set; }
        private void Record(Guid id, CancellationToken token) { Calls++; AccountId = id; Token = token; }
        private CoinTransactionReceipt Receipt(Guid id, CoinTransactionType type) => new(Guid.NewGuid(), id, 10, 10, type, "test", "r10", null, Now);
        public Task<CoinWallet?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
        { Record(accountId, cancellationToken); return Task.FromResult<CoinWallet?>(null); }
        public Task<CoinWallet> CreateWalletAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken = default)
        { Record(accountId, cancellationToken); return Task.FromResult(CoinWallet.Create(accountId, now)); }
        public Task<CoinTransactionRecord?> FindTransactionByIdempotencyKeyAsync(Guid accountId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected ledger lookup.");
        public Task<CoinTransactionReceipt> ExecuteCreditAsync(Guid accountId, long amount, CoinTransactionType type, string reason, string idempotencyKey, string? referenceId, DateTimeOffset now, CancellationToken cancellationToken = default)
        { Record(accountId, cancellationToken); return Task.FromResult(Receipt(accountId, type)); }
        public Task<CoinTransactionReceipt> ExecuteDebitAsync(Guid accountId, long amount, CoinTransactionType type, string reason, string idempotencyKey, string? referenceId, DateTimeOffset now, CancellationToken cancellationToken = default)
        { Record(accountId, cancellationToken); return Task.FromResult(Receipt(accountId, type)); }
        public Task<CoinTransactionReceipt> ExecuteTransferAsync(Guid senderAccountId, Guid recipientAccountId, long amount, string idempotencyKey, string? note, DateTimeOffset now, CancellationToken cancellationToken = default)
        { Record(senderAccountId, cancellationToken); RecipientId = recipientAccountId; return Task.FromResult(Receipt(senderAccountId, CoinTransactionType.TransferDebit)); }
        public Task<IReadOnlyList<CoinTransactionReceipt>> GetHistoryAsync(Guid accountId, int limit, int offset, CancellationToken cancellationToken = default)
        { Record(accountId, cancellationToken); return Task.FromResult<IReadOnlyList<CoinTransactionReceipt>>([]); }
    }
}
