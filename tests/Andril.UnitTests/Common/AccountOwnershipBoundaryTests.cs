using Andril.Common.Accounts;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class AccountOwnershipBoundaryTests
{
    private static readonly Guid Subject = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherSubject = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignRepositoryResultNeverBecomesAnAccountDto(bool initialize)
    {
        var repository = new Accounts { Result = Account.Create(OtherSubject, DateTimeOffset.UtcNow) };
        var service = new AccountService(new User(), repository, TimeProvider.System);

        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => Invoke(service, initialize));

        Assert.Equal(Subject, repository.Subject);
        Assert.Equal(1, repository.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ChangedOrRevokedIdentityCannotReceiveAnEarlierLookup(bool initialize, bool revoke)
    {
        var user = new User();
        var repository = new Accounts { Result = Account.Create(Subject, DateTimeOffset.UtcNow), AfterLookup = () =>
        {
            if (revoke) user.IsAuthenticated = false;
            else user.UserId = OtherSubject;
        } };
        var service = new AccountService(user, repository, TimeProvider.System);

        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => Invoke(service, initialize));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CancellationBeforeOrDuringLookupIsPreserved(bool initialize, bool before)
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new Accounts { Result = Account.Create(Subject, DateTimeOffset.UtcNow), AfterLookup = cancellation.Cancel };
        if (before) cancellation.Cancel();
        var service = new AccountService(new User(), repository, TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(service, initialize, cancellation.Token));

        Assert.Equal(before ? 0 : 1, repository.Calls);
        if (!before) Assert.Equal(cancellation.Token, repository.Token);
    }

    [Fact]
    public async Task NullInitializationResultIsUnavailableInsteadOfAnInternalNullFailure()
    {
        var service = new AccountService(new User(), new Accounts(), TimeProvider.System);
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => service.InitializeAsync());
    }

    private static async Task Invoke(AccountService service, bool initialize, CancellationToken token = default)
    {
        if (initialize) await service.InitializeAsync(token);
        else await service.GetMeAsync(token);
    }

    private sealed class User : ICurrentUser
    {
        public Guid? UserId { get; set; } = Subject;
        public bool IsAuthenticated { get; set; } = true;
    }

    private sealed class Accounts : IAccountRepository
    {
        public Account? Result { get; init; }
        public Action? AfterLookup { get; init; }
        public Guid Subject { get; private set; }
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Calls++;
            Subject = subject;
            Token = cancellationToken;
            AfterLookup?.Invoke();
            return Task.FromResult(Result);
        }
        public async Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            (await FindBySubjectAsync(subject, cancellationToken))!;
    }
}
