using System.Collections.Concurrent;
using Andril.Common.Accounts;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class AccountServiceTests
{
    [Fact]
    public async Task UnauthenticatedCaller_CannotReadOrInitialize()
    {
        var repository = new MemoryAccounts();
        var service = new AccountService(new User(Guid.NewGuid(), false), repository, TimeProvider.System);
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => service.GetMeAsync());
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => service.InitializeAsync());
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptySubject_IsRejected(bool empty)
    {
        var repository = new MemoryAccounts();
        var service = new AccountService(new User(empty ? Guid.Empty : null), repository, TimeProvider.System);
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => service.InitializeAsync());
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task ReadingBeforeInitialization_DoesNotCreateAnAccount()
    {
        var repository = new MemoryAccounts();
        var service = new AccountService(new User(Guid.NewGuid()), repository, TimeProvider.System);
        Assert.Null(await service.GetMeAsync());
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task InitializationUsesCurrentSubject_AndIsIdempotent()
    {
        var subject = Guid.NewGuid();
        var repository = new MemoryAccounts();
        var service = new AccountService(new User(subject), repository, TimeProvider.System);
        var first = await service.InitializeAsync();
        Assert.Equal(first, await service.InitializeAsync());
        Assert.Equal(first, await service.GetMeAsync());
        Assert.Equal(subject, Assert.Single(repository.Rows).Key);
    }

    [Fact]
    public async Task UsersHaveSeparateAccounts_AndDtoExcludesSubject()
    {
        var repository = new MemoryAccounts();
        var first = new AccountService(new User(Guid.NewGuid()), repository, TimeProvider.System);
        var second = new AccountService(new User(Guid.NewGuid()), repository, TimeProvider.System);
        var a = await first.InitializeAsync();
        Assert.Null(await second.GetMeAsync());
        var b = await second.InitializeAsync();
        Assert.NotEqual(a.AccountId, b.AccountId);
        Assert.Equal(a, await first.GetMeAsync());
        Assert.Equal(new[] { "AccountId", "CreatedAtUtc", "UpdatedAtUtc" },
            typeof(AccountDto).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public void EntityRejectsEmptyIdentity_AndNormalizesUtc()
    {
        Assert.Throws<ArgumentException>(() => Account.Create(Guid.Empty, DateTimeOffset.UtcNow));
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.FromHours(5.5));
        var account = Account.Create(Guid.NewGuid(), now);
        Assert.NotEqual(Guid.Empty, account.Id);
        Assert.Equal(TimeSpan.Zero, account.CreatedAtUtc.Offset);
        Assert.Equal(now.ToUniversalTime(), account.CreatedAtUtc);
        Assert.Equal(account.CreatedAtUtc, account.UpdatedAtUtc);
    }

    private sealed record User(Guid? UserId, bool IsAuthenticated = true) : ICurrentUser;
    private sealed class MemoryAccounts : IAccountRepository
    {
        internal ConcurrentDictionary<Guid, Account> Rows { get; } = new();
        internal int Calls;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Rows.GetValueOrDefault(subject));
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Rows.GetOrAdd(subject, id => Account.Create(id, now)));
        }
    }
}
