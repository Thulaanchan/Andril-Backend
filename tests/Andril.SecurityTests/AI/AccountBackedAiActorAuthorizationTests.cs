
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.API.Integrations.AI;
using Andril.Common.Accounts;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.SecurityTests.AI;

/// <summary>
/// AI-BE-02 authorization tests using synthetic accounts
/// and an in-memory profile-access stub.
///
/// These tests do not prove real database ownership,
/// JWT verification or Social eligibility enforcement.
/// </summary>
public sealed class AccountBackedAiActorAuthorizationTests
{
    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task AuthorizedAccountAndProfile_ReturnsCorrectModuleScopedActor(
        AiModule module)
    {
        var subject = Guid.NewGuid();
        var account = Account.Create(
            subject, DateTimeOffset.UtcNow);

        var profileId = Guid.NewGuid();

        var accounts = new RecordingAccountRepository(account);
        var profiles = new RecordingModuleProfileReader(profileId);

        var sut = CreateSut(
            new TestCurrentUser(subject, true),
            accounts,
            profiles);

        var actor = await sut.GetAuthorizedActorAsync(module);

        Assert.NotNull(actor);
        Assert.Equal(account.Id, actor.UserId);
        Assert.Equal(profileId, actor.ModuleProfileId);
        Assert.Equal(module, actor.Module);

        Assert.Equal(subject, accounts.LastSubject);
        Assert.Equal(account.Id, profiles.LastAccountId);
        Assert.Equal(module, profiles.LastModule);

        Assert.Equal(1, accounts.FindCalls);
        Assert.Equal(0, accounts.GetOrCreateCalls);
        Assert.Equal(1, profiles.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnauthenticatedUser_DeniedWithoutRepositoryOrProfileAccess(
        bool hasSubject)
    {
        Guid? subject = hasSubject ? Guid.NewGuid() : null;

        var accounts = new RecordingAccountRepository();
        var profiles = new RecordingModuleProfileReader(
            Guid.NewGuid());

        var sut = CreateSut(
            new TestCurrentUser(subject, false),
            accounts,
            profiles);

        var actor = await sut.GetAuthorizedActorAsync(
            AiModule.Learning);

        Assert.Null(actor);
        AssertNoDatabaseAccess(accounts, profiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyAuthenticatedSubject_DeniedWithoutDatabaseAccess(
        bool emptyGuid)
    {
        Guid? subject = emptyGuid ? Guid.Empty : null;

        var accounts = new RecordingAccountRepository();
        var profiles = new RecordingModuleProfileReader(
            Guid.NewGuid());

        var sut = CreateSut(
            new TestCurrentUser(subject, true),
            accounts,
            profiles);

        var actor = await sut.GetAuthorizedActorAsync(
            AiModule.Hobbies);

        Assert.Null(actor);
        AssertNoDatabaseAccess(accounts, profiles);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task UnknownModule_DeniedBeforeAccountLookup(
        int rawModule)
    {
        var accounts = new RecordingAccountRepository();
        var profiles = new RecordingModuleProfileReader(
            Guid.NewGuid());

        var sut = CreateSut(
            new TestCurrentUser(Guid.NewGuid(), true),
            accounts,
            profiles);

        var actor = await sut.GetAuthorizedActorAsync(
            (AiModule)rawModule);

        Assert.Null(actor);
        AssertNoDatabaseAccess(accounts, profiles);
    }

    [Fact]
    public async Task UninitializedAccount_IsDeniedAndNeverCreated()
    {
        var subject = Guid.NewGuid();

        var accounts = new RecordingAccountRepository();
        var profiles = new RecordingModuleProfileReader(
            Guid.NewGuid());

        var sut = CreateSut(
            new TestCurrentUser(subject, true),
            accounts,
            profiles);

        var actor = await sut.GetAuthorizedActorAsync(
            AiModule.Learning);

        Assert.Null(actor);
        Assert.Equal(1, accounts.FindCalls);
        Assert.Equal(subject, accounts.LastSubject);
        Assert.Equal(0, accounts.GetOrCreateCalls);
        Assert.Equal(0, profiles.Calls);
    }

    [Fact]
    public async Task AccountWithDifferentSupabaseSubject_IsDeniedBeforeProfileLookup()
    {
        var authenticatedSubject = Guid.NewGuid();

        var unrelatedAccount = Account.Create(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

        var accounts = new RecordingAccountRepository(
            unrelatedAccount);

        var profiles = new RecordingModuleProfileReader(
            Guid.NewGuid());

        var sut = CreateSut(
            new TestCurrentUser(authenticatedSubject, true),
            accounts,
            profiles);

        var actor = await sut.GetAuthorizedActorAsync(
            AiModule.Social);

        Assert.Null(actor);
        Assert.Equal(
            authenticatedSubject,
            accounts.LastSubject);

        Assert.Equal(1, accounts.FindCalls);
        Assert.Equal(0, accounts.GetOrCreateCalls);
        Assert.Equal(0, profiles.Calls);
    }

    [Theory]
    [InlineData(AiModule.Learning, false)]
    [InlineData(AiModule.Hobbies, true)]
    [InlineData(AiModule.Social, false)]
    public async Task MissingOrEmptyModuleProfile_IsDenied(
        AiModule module,
        bool emptyGuid)
    {
        var subject = Guid.NewGuid();
        var account = Account.Create(
            subject, DateTimeOffset.UtcNow);

        var accounts = new RecordingAccountRepository(account);

        Guid? profileId = emptyGuid ? Guid.Empty : null;

        var profiles = new RecordingModuleProfileReader(
            profileId);

        var sut = CreateSut(
            new TestCurrentUser(subject, true),
            accounts,
            profiles);

        var actor = await sut.GetAuthorizedActorAsync(module);

        Assert.Null(actor);
        Assert.Equal(account.Id, profiles.LastAccountId);
        Assert.Equal(module, profiles.LastModule);
        Assert.Equal(1, profiles.Calls);
        Assert.Equal(0, accounts.GetOrCreateCalls);
    }

    [Fact]
    public async Task ProfileReaderFailure_IsNotConvertedIntoAccessGranted()
    {
        var subject = Guid.NewGuid();

        var account = Account.Create(
            subject, DateTimeOffset.UtcNow);

        var accounts = new RecordingAccountRepository(account);

        var profiles = new RecordingModuleProfileReader(
            Guid.NewGuid())
        {
            Failure = new InvalidOperationException(
                "Simulated profile store outage")
        };

        var sut = CreateSut(
            new TestCurrentUser(subject, true),
            accounts,
            profiles);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetAuthorizedActorAsync(AiModule.Social));

        Assert.Equal(1, profiles.Calls);
        Assert.Equal(0, accounts.GetOrCreateCalls);
    }

    [Fact]
    public async Task AccountRepositoryFailure_DoesNotAttemptProfileLookup()
    {
        var accounts = new RecordingAccountRepository()
        {
            Failure = new InvalidOperationException(
                "Simulated account store outage")
        };

        var profiles = new RecordingModuleProfileReader(
            Guid.NewGuid());

        var sut = CreateSut(
            new TestCurrentUser(Guid.NewGuid(), true),
            accounts,
            profiles);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetAuthorizedActorAsync(AiModule.Learning));

        Assert.Equal(1, accounts.FindCalls);
        Assert.Equal(0, accounts.GetOrCreateCalls);
        Assert.Equal(0, profiles.Calls);
    }

    [Fact]
    public async Task CancelledRequest_DoesNotCallEitherRepository()
    {
        var accounts = new RecordingAccountRepository();
        var profiles = new RecordingModuleProfileReader(
            Guid.NewGuid());

        var sut = CreateSut(
            new TestCurrentUser(Guid.NewGuid(), true),
            accounts,
            profiles);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GetAuthorizedActorAsync(
                AiModule.Learning,
                cts.Token));

        AssertNoDatabaseAccess(accounts, profiles);
    }

    private static AccountBackedAiActorAuthorization CreateSut(
        ICurrentUser currentUser,
        IAccountRepository accounts,
        IAiModuleProfileAccessReader profiles) =>
        new(currentUser, accounts, profiles);

    private static void AssertNoDatabaseAccess(
        RecordingAccountRepository accounts,
        RecordingModuleProfileReader profiles)
    {
        Assert.Equal(0, accounts.FindCalls);
        Assert.Equal(0, accounts.GetOrCreateCalls);
        Assert.Equal(0, profiles.Calls);
    }

    private sealed record TestCurrentUser(
        Guid? UserId,
        bool IsAuthenticated) : ICurrentUser;

    private sealed class RecordingAccountRepository(
        Account? accountToReturn = null) : IAccountRepository
    {
        public int FindCalls { get; private set; }
        public int GetOrCreateCalls { get; private set; }
        public Guid? LastSubject { get; private set; }
        public Exception? Failure { get; init; }

        public Task<Account?> FindBySubjectAsync(
            Guid subject,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;
            LastSubject = subject;

            if (Failure is not null)
                throw Failure;

            return Task.FromResult(accountToReturn);
        }

        public Task<Account> GetOrCreateAsync(
            Guid subject,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            GetOrCreateCalls++;

            throw new InvalidOperationException(
                "Authorization must never initialize accounts.");
        }
    }

    private sealed class RecordingModuleProfileReader(
        Guid? profileId = null) : IAiModuleProfileAccessReader
    {
        public int Calls { get; private set; }
        public Guid? LastAccountId { get; private set; }
        public AiModule? LastModule { get; private set; }
        public Exception? Failure { get; init; }

        public Task<Guid?> FindAuthorizedProfileIdAsync(
            Guid accountId,
            AiModule module,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAccountId = accountId;
            LastModule = module;

            if (Failure is not null)
                throw Failure;

            return Task.FromResult(profileId);
        }
    }
}
