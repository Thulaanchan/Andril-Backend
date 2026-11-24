using Andril.API.Authorization.Hobbies;
using Andril.Common.Accounts;
using Andril.Hobbies.Interfaces;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.SecurityTests.Authorization.Hobbies;

public sealed class AccountBackedGalleryActorResolverTests
{
    private static readonly Guid SubjectId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherSubjectId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnonymousUser_IsDeniedWithoutLookups()
    {
        var accounts = new FakeAccountRepository();
        var profiles = new FakeHobbiesGalleryProfileAccessReader();
        var resolver = new AccountBackedGalleryActorResolver(new FakeCurrentUser(SubjectId, false), accounts, profiles);

        var actor = await resolver.ResolveAsync();

        Assert.Null(actor);
        AssertNoLookups(accounts, profiles);
    }

    [Fact]
    public async Task MissingUserId_IsDeniedWithoutLookups()
    {
        var accounts = new FakeAccountRepository();
        var profiles = new FakeHobbiesGalleryProfileAccessReader();
        var resolver = new AccountBackedGalleryActorResolver(new FakeCurrentUser(null, true), accounts, profiles);

        var actor = await resolver.ResolveAsync();

        Assert.Null(actor);
        AssertNoLookups(accounts, profiles);
    }

    [Fact]
    public async Task EmptyUserId_IsDeniedWithoutLookups()
    {
        var accounts = new FakeAccountRepository();
        var profiles = new FakeHobbiesGalleryProfileAccessReader();
        var resolver = new AccountBackedGalleryActorResolver(new FakeCurrentUser(Guid.Empty, true), accounts, profiles);

        var actor = await resolver.ResolveAsync();

        Assert.Null(actor);
        AssertNoLookups(accounts, profiles);
    }

    [Fact]
    public async Task MissingAccount_IsDeniedWithoutProfileLookupOrAccountCreation()
    {
        var accounts = new FakeAccountRepository();
        var profiles = new FakeHobbiesGalleryProfileAccessReader(ProfileId);
        var resolver = CreateResolver(accounts, profiles);

        var actor = await resolver.ResolveAsync();

        Assert.Null(actor);
        Assert.Equal(SubjectId, Assert.Single(accounts.Lookups).Subject);
        Assert.Empty(profiles.Lookups);
        Assert.Equal(0, accounts.GetOrCreateCalls);
    }

    [Fact]
    public async Task MismatchedSupabaseSubject_IsDeniedWithoutProfileLookup()
    {
        var accounts = new FakeAccountRepository(Account.Create(OtherSubjectId, CreatedAt));
        var profiles = new FakeHobbiesGalleryProfileAccessReader(ProfileId);
        var resolver = CreateResolver(accounts, profiles);

        var actor = await resolver.ResolveAsync();

        Assert.Null(actor);
        Assert.Equal(SubjectId, Assert.Single(accounts.Lookups).Subject);
        Assert.Empty(profiles.Lookups);
        Assert.Equal(0, accounts.GetOrCreateCalls);
    }

    [Fact]
    public async Task MissingHobbiesProfile_IsDeniedAfterLookupByInternalAccountId()
    {
        var account = Account.Create(SubjectId, CreatedAt);
        var accounts = new FakeAccountRepository(account);
        var profiles = new FakeHobbiesGalleryProfileAccessReader();
        var resolver = CreateResolver(accounts, profiles);

        var actor = await resolver.ResolveAsync();

        Assert.Null(actor);
        var lookup = Assert.Single(profiles.Lookups);
        Assert.Equal(account.Id, lookup.AccountId);
        Assert.NotEqual(account.SupabaseUserId, lookup.AccountId);
    }

    [Fact]
    public async Task EmptyHobbiesProfileId_IsDenied()
    {
        var account = Account.Create(SubjectId, CreatedAt);
        var accounts = new FakeAccountRepository(account);
        var profiles = new FakeHobbiesGalleryProfileAccessReader(Guid.Empty);
        var resolver = CreateResolver(accounts, profiles);

        var actor = await resolver.ResolveAsync();

        Assert.Null(actor);
        Assert.Equal(account.Id, Assert.Single(profiles.Lookups).AccountId);
    }

    [Fact]
    public async Task TrustedReaderProfile_ReturnsActiveHobbiesActorWithDistinctIdentityIds()
    {
        var account = Account.Create(SubjectId, CreatedAt);
        var accounts = new FakeAccountRepository(account);
        var profiles = new FakeHobbiesGalleryProfileAccessReader(ProfileId);
        var resolver = CreateResolver(accounts, profiles);

        var actor = await resolver.ResolveAsync();

        Assert.NotNull(actor);
        Assert.Equal("Hobbies", actor.Module);
        Assert.True(actor.IsActive);
        Assert.Equal(ProfileId, actor.ModuleProfileId);
        Assert.Equal(account.SupabaseUserId, Assert.Single(accounts.Lookups).Subject);
        Assert.Equal(account.Id, Assert.Single(profiles.Lookups).AccountId);
        Assert.NotEqual(account.SupabaseUserId, account.Id);
        Assert.NotEqual(account.SupabaseUserId, actor.ModuleProfileId);
        Assert.NotEqual(account.Id, actor.ModuleProfileId);
        Assert.Equal(0, accounts.GetOrCreateCalls);
    }

    [Fact]
    public async Task PreCancelledRequest_ThrowsWithOriginalTokenWithoutLookups()
    {
        using var cancellation = new CancellationTokenSource();
        var accounts = new FakeAccountRepository(Account.Create(SubjectId, CreatedAt));
        var profiles = new FakeHobbiesGalleryProfileAccessReader(ProfileId);
        var resolver = CreateResolver(accounts, profiles);
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        AssertNoLookups(accounts, profiles);
    }

    [Fact]
    public async Task CancellationDuringAccountLookup_DiscardsLateSuccessWithoutProfileLookup()
    {
        using var cancellation = new CancellationTokenSource();
        var account = Account.Create(SubjectId, CreatedAt);
        var completion = new TaskCompletionSource<Account?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = new FakeAccountRepository { PendingResponse = completion.Task };
        var profiles = new FakeHobbiesGalleryProfileAccessReader(ProfileId);
        var attempt = CreateResolver(accounts, profiles).ResolveAsync(cancellation.Token);
        try
        {
            await accounts.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(attempt.IsCompleted);
            Assert.Equal((SubjectId, cancellation.Token), Assert.Single(accounts.Lookups));
            cancellation.Cancel();
            // The dependency deliberately ignores cancellation and returns valid identity evidence.
            completion.SetResult(account);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Empty(profiles.Lookups);
            Assert.Equal(0, accounts.GetOrCreateCalls);
        }
        finally
        {
            completion.TrySetResult(account);
        }
    }

    [Fact]
    public async Task CancellationDuringProfileLookup_DiscardsLateSuccessWithoutReturningActor()
    {
        using var cancellation = new CancellationTokenSource();
        var account = Account.Create(SubjectId, CreatedAt);
        var accounts = new FakeAccountRepository(account);
        var completion = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var profiles = new FakeHobbiesGalleryProfileAccessReader { PendingResponse = completion.Task };
        var attempt = CreateResolver(accounts, profiles).ResolveAsync(cancellation.Token);
        try
        {
            await profiles.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(attempt.IsCompleted);
            Assert.Equal((SubjectId, cancellation.Token), Assert.Single(accounts.Lookups));
            Assert.Equal((account.Id, cancellation.Token), Assert.Single(profiles.Lookups));
            cancellation.Cancel();
            completion.SetResult(ProfileId);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(0, accounts.GetOrCreateCalls);
        }
        finally
        {
            completion.TrySetResult(ProfileId);
        }
    }

    [Fact]
    public async Task CancellationToken_IsForwardedToBothLookups()
    {
        using var cancellation = new CancellationTokenSource();
        var accounts = new FakeAccountRepository(Account.Create(SubjectId, CreatedAt));
        var profiles = new FakeHobbiesGalleryProfileAccessReader(ProfileId);
        var resolver = CreateResolver(accounts, profiles);

        await resolver.ResolveAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, Assert.Single(accounts.Lookups).Token);
        Assert.Equal(cancellation.Token, Assert.Single(profiles.Lookups).Token);
    }

    [Fact]
    public async Task AccountLookupFailure_PropagatesWithoutProfileLookupOrAccountCreation()
    {
        var failure = new InvalidOperationException("Synthetic account lookup failure.");
        var accounts = new FakeAccountRepository(failure: failure);
        var profiles = new FakeHobbiesGalleryProfileAccessReader(ProfileId);
        var resolver = CreateResolver(accounts, profiles);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync());

        Assert.Same(failure, thrown);
        Assert.Equal(SubjectId, Assert.Single(accounts.Lookups).Subject);
        Assert.Empty(profiles.Lookups);
        Assert.Equal(0, accounts.GetOrCreateCalls);
    }

    [Fact]
    public async Task ProfileLookupFailure_PropagatesWithoutFallback()
    {
        var failure = new InvalidOperationException("Synthetic profile lookup failure.");
        var account = Account.Create(SubjectId, CreatedAt);
        var accounts = new FakeAccountRepository(account);
        var profiles = new FakeHobbiesGalleryProfileAccessReader(failure: failure);
        var resolver = CreateResolver(accounts, profiles);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync());

        Assert.Same(failure, thrown);
        Assert.Equal(account.Id, Assert.Single(profiles.Lookups).AccountId);
        Assert.Equal(0, accounts.GetOrCreateCalls);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var currentUser = new FakeCurrentUser(SubjectId, true);
        var accounts = new FakeAccountRepository();
        var profiles = new FakeHobbiesGalleryProfileAccessReader();

        Assert.Throws<ArgumentNullException>("currentUser", () => new AccountBackedGalleryActorResolver(null!, accounts, profiles));
        Assert.Throws<ArgumentNullException>("accounts", () => new AccountBackedGalleryActorResolver(currentUser, null!, profiles));
        Assert.Throws<ArgumentNullException>("profiles", () => new AccountBackedGalleryActorResolver(currentUser, accounts, null!));
    }

    private static AccountBackedGalleryActorResolver CreateResolver(
        FakeAccountRepository accounts,
        FakeHobbiesGalleryProfileAccessReader profiles) =>
        new(new FakeCurrentUser(SubjectId, true), accounts, profiles);

    private static void AssertNoLookups(
        FakeAccountRepository accounts,
        FakeHobbiesGalleryProfileAccessReader profiles)
    {
        Assert.Empty(accounts.Lookups);
        Assert.Empty(profiles.Lookups);
        Assert.Equal(0, accounts.GetOrCreateCalls);
    }

    private sealed record FakeCurrentUser(Guid? UserId, bool IsAuthenticated) : ICurrentUser;

    private sealed class FakeAccountRepository(Account? account = null, Exception? failure = null) : IAccountRepository
    {
        public Task<Account?>? PendingResponse { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(Guid Subject, CancellationToken Token)> Lookups { get; } = [];
        public int GetOrCreateCalls { get; private set; }

        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Lookups.Add((subject, cancellationToken));
            Started.TrySetResult();
            return PendingResponse ?? (failure is null ? Task.FromResult(account) : Task.FromException<Account?>(failure));
        }

        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            GetOrCreateCalls++;
            throw new InvalidOperationException("Gallery authorization must never create an account.");
        }
    }

    private sealed class FakeHobbiesGalleryProfileAccessReader(Guid? profileId = null, Exception? failure = null)
        : IHobbiesGalleryProfileAccessReader
    {
        public Task<Guid?>? PendingResponse { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(Guid AccountId, CancellationToken Token)> Lookups { get; } = [];

        public Task<Guid?> FindActiveOwnedHobbiesProfileIdAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Lookups.Add((accountId, cancellationToken));
            Started.TrySetResult();
            return PendingResponse ?? (failure is null ? Task.FromResult(profileId) : Task.FromException<Guid?>(failure));
        }
    }
}
