
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Hobbies;
using Andril.Common.Accounts;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Map.Places;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.SecurityTests.Authorization.Hobbies;

public sealed class AccountBackedMapCaptureSessionAccessReaderTests
{
    private static readonly Guid SubjectId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly Guid OtherSubjectId =
        Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static readonly Guid ProfileId =
        Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static readonly Guid AttractionId =
        Guid.Parse("00000000-0000-0000-0000-000000000004");

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // TEST 01
    [Fact]
    public async Task EmptyAttractionId_IsDeniedWithoutLookups()
    {
        var fixture = new TestFixture();

        var result = await fixture.Reader
            .ResolveAuthorizedProfileIdAsync(Guid.Empty);

        Assert.Null(result);
        AssertNoLookups(fixture);
    }

    // TEST 02
    [Fact]
    public async Task AnonymousUser_IsDeniedWithoutLookups()
    {
        var fixture = new TestFixture(
            new FakeCurrentUser(SubjectId, false));

        var result = await fixture.Reader
            .ResolveAuthorizedProfileIdAsync(AttractionId);

        Assert.Null(result);
        AssertNoLookups(fixture);
    }

    // TEST 03-04
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidUserId_IsDeniedWithoutLookups(
        bool useEmptyId)
    {
        Guid? userId = useEmptyId ? Guid.Empty : null;

        var fixture = new TestFixture(
            new FakeCurrentUser(userId, true));

        var result = await fixture.Reader
            .ResolveAuthorizedProfileIdAsync(AttractionId);

        Assert.Null(result);
        AssertNoLookups(fixture);
    }

    // TEST 05
    [Fact]
    public async Task MissingAccount_IsDenied()
    {
        var fixture = new TestFixture();
        fixture.Accounts.AccountToReturn = null;

        var result = await fixture.Reader
            .ResolveAuthorizedProfileIdAsync(AttractionId);

        Assert.Null(result);

        Assert.Equal(
            SubjectId,
            Assert.Single(fixture.Accounts.Lookups).Subject);

        Assert.Empty(fixture.Profiles.Lookups);
        Assert.Empty(fixture.Attractions.Lookups);
        Assert.Equal(0, fixture.Accounts.GetOrCreateCalls);
    }

    // TEST 06
    [Fact]
    public async Task MismatchedSubject_IsDenied()
    {
        var fixture = new TestFixture();

        fixture.Accounts.AccountToReturn =
            Account.Create(OtherSubjectId, CreatedAt);

        var result = await fixture.Reader
            .ResolveAuthorizedProfileIdAsync(AttractionId);

        Assert.Null(result);
        Assert.Single(fixture.Accounts.Lookups);
        Assert.Empty(fixture.Profiles.Lookups);
        Assert.Empty(fixture.Attractions.Lookups);
        Assert.Equal(0, fixture.Accounts.GetOrCreateCalls);
    }

    // TEST 07-08
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidHobbiesProfile_IsDenied(
        bool useEmptyId)
    {
        var fixture = new TestFixture();

        fixture.Profiles.ProfileToReturn =
            useEmptyId ? Guid.Empty : null;

        var result = await fixture.Reader
            .ResolveAuthorizedProfileIdAsync(AttractionId);

        Assert.Null(result);

        Assert.Equal(
            fixture.Accounts.AccountToReturn!.Id,
            Assert.Single(fixture.Profiles.Lookups).AccountId);

        Assert.Empty(fixture.Attractions.Lookups);
        Assert.Equal(0, fixture.Accounts.GetOrCreateCalls);
    }

    // TEST 09
    [Fact]
    public async Task IneligibleAttraction_IsDenied()
    {
        var fixture = new TestFixture();
        fixture.Attractions.IsEligible = false;

        var result = await fixture.Reader
            .ResolveAuthorizedProfileIdAsync(AttractionId);

        Assert.Null(result);

        var lookup =
            Assert.Single(fixture.Attractions.Lookups);

        Assert.Equal(AttractionId, lookup.AttractionId);
        Assert.Equal(ProfileId, lookup.ProfileId);

        Assert.Equal(0, fixture.Accounts.GetOrCreateCalls);
    }

    // TEST 10
    [Fact]
    public async Task AuthorizedContributor_ReturnsHobbiesProfileId()
    {
        var fixture = new TestFixture();

        using var cancellation =
            new CancellationTokenSource();

        var result = await fixture.Reader
            .ResolveAuthorizedProfileIdAsync(
                AttractionId,
                cancellation.Token);

        Assert.Equal<Guid?>(ProfileId, result);

        var account = fixture.Accounts.AccountToReturn!;

        Assert.NotEqual<Guid?>(account.Id, result);
        Assert.NotEqual<Guid?>(account.SupabaseUserId, result);

        var accountLookup =
            Assert.Single(fixture.Accounts.Lookups);

        var profileLookup =
            Assert.Single(fixture.Profiles.Lookups);

        var attractionLookup =
            Assert.Single(fixture.Attractions.Lookups);

        Assert.Equal(SubjectId, accountLookup.Subject);
        Assert.Equal(account.Id, profileLookup.AccountId);

        Assert.Equal(
            AttractionId,
            attractionLookup.AttractionId);

        Assert.Equal(ProfileId, attractionLookup.ProfileId);

        Assert.Equal(
            cancellation.Token,
            accountLookup.Token);

        Assert.Equal(
            cancellation.Token,
            profileLookup.Token);

        Assert.Equal(
            cancellation.Token,
            attractionLookup.Token);

        Assert.Equal(0, fixture.Accounts.GetOrCreateCalls);
    }

    // TEST 11-13
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DependencyFailure_DoesNotAuthorize(
        int failureStage)
    {
        var fixture = new TestFixture();

        var failure = new InvalidOperationException(
            "Synthetic authorization dependency failure.");

        if (failureStage == 1)
            fixture.Accounts.Failure = failure;

        if (failureStage == 2)
            fixture.Profiles.Failure = failure;

        if (failureStage == 3)
            fixture.Attractions.Failure = failure;

        var thrown =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Reader
                    .ResolveAuthorizedProfileIdAsync(
                        AttractionId));

        Assert.Same(failure, thrown);

        Assert.Single(fixture.Accounts.Lookups);

        Assert.Equal(
            failureStage >= 2 ? 1 : 0,
            fixture.Profiles.Lookups.Count);

        Assert.Equal(
            failureStage == 3 ? 1 : 0,
            fixture.Attractions.Lookups.Count);

        Assert.Equal(0, fixture.Accounts.GetOrCreateCalls);
    }

    // TEST 14
    [Fact]
    public async Task CancelledRequest_PerformsNoLookups()
    {
        var fixture = new TestFixture();

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Reader
                .ResolveAuthorizedProfileIdAsync(
                    AttractionId,
                    cancellation.Token));

        AssertNoLookups(fixture);
    }

    // TEST 15-17
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CancellationDuringLookup_DoesNotAuthorize(
        int cancellationStage)
    {
        var fixture = new TestFixture();

        using var cancellation =
            new CancellationTokenSource();

        if (cancellationStage == 1)
            fixture.Accounts.AfterLookup = cancellation.Cancel;

        if (cancellationStage == 2)
            fixture.Profiles.AfterLookup = cancellation.Cancel;

        if (cancellationStage == 3)
            fixture.Attractions.AfterLookup = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Reader
                .ResolveAuthorizedProfileIdAsync(
                    AttractionId,
                    cancellation.Token));

        Assert.Single(fixture.Accounts.Lookups);

        Assert.Equal(
            cancellationStage >= 2 ? 1 : 0,
            fixture.Profiles.Lookups.Count);

        Assert.Equal(
            cancellationStage == 3 ? 1 : 0,
            fixture.Attractions.Lookups.Count);

        Assert.Equal(0, fixture.Accounts.GetOrCreateCalls);
    }

    // TEST 18
    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var currentUser =
            new FakeCurrentUser(SubjectId, true);

        var accounts = new FakeAccountRepository();
        var profiles = new FakeProfileAccessReader();
        var attractions = new FakeAttractionAccessReader();

        Assert.Throws<ArgumentNullException>(
            () => new AccountBackedMapCaptureSessionAccessReader(
                null!, accounts, profiles, attractions));

        Assert.Throws<ArgumentNullException>(
            () => new AccountBackedMapCaptureSessionAccessReader(
                currentUser, null!, profiles, attractions));

        Assert.Throws<ArgumentNullException>(
            () => new AccountBackedMapCaptureSessionAccessReader(
                currentUser, accounts, null!, attractions));

        Assert.Throws<ArgumentNullException>(
            () => new AccountBackedMapCaptureSessionAccessReader(
                currentUser, accounts, profiles, null!));
    }

    private static void AssertNoLookups(
        TestFixture fixture)
    {
        Assert.Empty(fixture.Accounts.Lookups);
        Assert.Empty(fixture.Profiles.Lookups);
        Assert.Empty(fixture.Attractions.Lookups);
        Assert.Equal(0, fixture.Accounts.GetOrCreateCalls);
    }

    // Shared test setup.
    private sealed class TestFixture
    {
        public FakeAccountRepository Accounts { get; } = new();

        public FakeProfileAccessReader Profiles { get; } = new();

        public FakeAttractionAccessReader Attractions { get; } = new();

        public AccountBackedMapCaptureSessionAccessReader Reader
        {
            get;
        }

        public TestFixture(ICurrentUser? currentUser = null)
        {
            Reader = new AccountBackedMapCaptureSessionAccessReader(
                currentUser ?? new FakeCurrentUser(SubjectId, true),
                Accounts,
                Profiles,
                Attractions);
        }
    }

    // Fake authenticated identity.
    private sealed record FakeCurrentUser(
        Guid? UserId,
        bool IsAuthenticated) : ICurrentUser;

    // Fake existing account repository.
    private sealed class FakeAccountRepository
        : IAccountRepository
    {
        public Account? AccountToReturn { get; set; } =
            Account.Create(SubjectId, CreatedAt);

        public List<(Guid Subject, CancellationToken Token)>
            Lookups
        { get; } = new();

        public int GetOrCreateCalls { get; private set; }

        public Exception? Failure { get; set; }

        public Action? AfterLookup { get; set; }

        public Task<Account?> FindBySubjectAsync(
            Guid subject,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Lookups.Add((subject, cancellationToken));

            if (Failure is Exception failure)
                return Task.FromException<Account?>(failure);

            AfterLookup?.Invoke();

            return Task.FromResult(AccountToReturn);
        }

        public Task<Account> GetOrCreateAsync(
            Guid subject,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            GetOrCreateCalls++;

            throw new InvalidOperationException(
                "Map authorization must not create accounts.");
        }
    }

    // Fake active Hobbies profile ownership reader.
    private sealed class FakeProfileAccessReader
        : IHobbiesGalleryProfileAccessReader
    {
        public Guid? ProfileToReturn { get; set; } = ProfileId;

        public List<(Guid AccountId, CancellationToken Token)>
            Lookups
        { get; } = new();

        public Exception? Failure { get; set; }

        public Action? AfterLookup { get; set; }

        public Task<Guid?> FindActiveOwnedHobbiesProfileIdAsync(
            Guid accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Lookups.Add((accountId, cancellationToken));

            if (Failure is Exception failure)
                return Task.FromException<Guid?>(failure);

            AfterLookup?.Invoke();

            return Task.FromResult(ProfileToReturn);
        }
    }

    // Fake canonical attraction eligibility reader.
    private sealed class FakeAttractionAccessReader
        : IMapContributionAttractionAccessReader
    {
        public bool IsEligible { get; set; } = true;

        public List<(
            Guid AttractionId,
            Guid ProfileId,
            CancellationToken Token)> Lookups
        { get; } = new();

        public Exception? Failure { get; set; }

        public Action? AfterLookup { get; set; }

        public Task<bool> IsEligibleForContributionAsync(
            Guid attractionId,
            Guid ownerHobbiesModuleProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Lookups.Add((
                attractionId,
                ownerHobbiesModuleProfileId,
                cancellationToken));

            if (Failure is Exception failure)
                return Task.FromException<bool>(failure);

            AfterLookup?.Invoke();

            return Task.FromResult(IsEligible);
        }
    }
}
