using Andril.AI.ChatBox;
using Andril.API.Integrations.AI;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.SecurityTests.AI;

public sealed class CurrentLearningAiModuleProfileAccessReaderTests
{
    private sealed class User : ICurrentUser
    {
        public Guid? UserId { get; set; } = Guid.NewGuid();
        public bool IsAuthenticated { get; set; } = true;
    }

    private sealed class Accounts(Account account) : IAccountRepository
    {
        public int Reads;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            return Task.FromResult<Account?>(account);
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provisioning is forbidden in this reader.");
    }

    private sealed class Profiles : IPrivateProfileService
    {
        public PrivateProfileDto Profile = new(Guid.NewGuid(), "learning", DateTimeOffset.UnixEpoch);
        public int Reads;
        public Func<Task>? BeforeRelease;
        public Exception? Failure;
        public async Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action,
            CancellationToken cancellationToken)
        {
            Assert.Equal("learning", module); cancellationToken.ThrowIfCancellationRequested(); Reads++;
            if (Failure is not null) throw Failure;
            var result = await action(Profile);
            if (BeforeRelease is not null) await BeforeRelease();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Uncoordinated profile reads are forbidden.");
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Profile creation is forbidden.");
    }

    private sealed class Access : IPrivateModuleAccess
    {
        public int Checks;
        public Exception? Failure;
        public Task RequireAsync(string module, CancellationToken cancellationToken = default)
        {
            Assert.Equal("learning", module); cancellationToken.ThrowIfCancellationRequested(); Checks++;
            if (Failure is not null) throw Failure;
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public User User { get; } = new();
        public Account Account { get; }
        public Accounts Accounts { get; }
        public Profiles Profiles { get; } = new();
        public Access Access { get; } = new();
        public CurrentLearningAiModuleProfileAccessReader Reader { get; }
        public Fixture()
        {
            Account = Account.Create(User.UserId!.Value, DateTimeOffset.UnixEpoch);
            Accounts = new(Account);
            Reader = new(User, Accounts, Profiles, Access);
        }
    }

    [Fact]
    public async Task CurrentOwnerIsResolvedThroughFreshAuthorizedProfileAndModuleChecks()
    {
        var f = new Fixture();
        for (var i = 0; i < 2; i++)
            Assert.Equal(f.Profiles.Profile.ProfileId, await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
        Assert.Equal(2, f.Accounts.Reads); Assert.Equal(2, f.Profiles.Reads); Assert.Equal(2, f.Access.Checks);
    }

    [Fact]
    public async Task ForeignAccountCannotSelectAnotherProfile()
    {
        var f = new Fixture();
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(Guid.NewGuid(), AiModule.Learning));
        Assert.Equal(0, f.Profiles.Reads); Assert.Equal(0, f.Access.Checks);
    }

    [Fact]
    public async Task UnauthenticatedAndUnsupportedModuleNeverReachAuthority()
    {
        var f = new Fixture();
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Hobbies));
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, (AiModule)999));
        f.User.IsAuthenticated = false;
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
        Assert.Equal(0, f.Accounts.Reads); Assert.Equal(0, f.Profiles.Reads);
    }

    [Fact]
    public async Task AccountSubjectMismatchDeniesBeforeProfileRead()
    {
        var f = new Fixture(); f.User.UserId = Guid.NewGuid();
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
        Assert.Equal(0, f.Profiles.Reads);
    }

    [Fact]
    public async Task ForeignModuleOrEmptyProfileIsNotReleased()
    {
        var f = new Fixture();
        f.Profiles.Profile = f.Profiles.Profile with { Module = "hobbies" };
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
        f.Profiles.Profile = f.Profiles.Profile with { Module = "learning", ProfileId = Guid.Empty };
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
        Assert.Equal(0, f.Access.Checks);
    }

    [Fact]
    public async Task InactiveAccessAndMissingProfileFailClosed()
    {
        var f = new Fixture(); f.Access.Failure = new ProfileAccessDeniedException();
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
        f.Access.Failure = null; f.Profiles.Failure = new ProfileMissingException();
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
    }

    [Fact]
    public async Task RevocationAtAuthorizedReadReleaseCannotExposeProfile()
    {
        var f = new Fixture();
        f.Profiles.BeforeRelease = () => throw new ProfileAccessDeniedException();
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
    }

    [Fact]
    public async Task AuthenticatedIdentityChangesBeforeReleaseDenyResult()
    {
        var f = new Fixture();
        f.Profiles.BeforeRelease = () => { f.User.UserId = Guid.NewGuid(); return Task.CompletedTask; };
        Assert.Null(await f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
    }

    [Fact]
    public async Task UnavailableAuthorityRemainsUnavailableWithoutFallback()
    {
        var f = new Fixture(); f.Profiles.Failure = new ProfileContractPendingException();
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning));
        Assert.Equal(0, f.Access.Checks);
    }

    [Fact]
    public async Task CancellationAtReleasePropagatesWithoutProfileResult()
    {
        var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        f.Profiles.BeforeRelease = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Reader.FindAuthorizedProfileIdAsync(f.Account.Id, AiModule.Learning, cancellation.Token));
    }
}
