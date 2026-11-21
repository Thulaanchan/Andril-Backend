using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Moderation;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.SecurityTests.Moderation;

public sealed class TrustedModerationReporterProfileAccessReaderTests
{
    private static readonly Guid Subject =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid ReporterProfileId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly DateTimeOffset Now =
        new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    private static ModerationReport Report(
        string module = "learning")
    {
        return new ModerationReport(
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            ReporterProfileId,
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            "profile",
            module,
            Now);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task ValidFixture_AllowsReporterCheck(
        string module)
    {
        var fixture = new Fixture();

        fixture.Profile = new PrivateProfileDto(
            ReporterProfileId, module, Now);

        Assert.True(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report(module)));

        Assert.Equal(1, fixture.AccountCalls);
        Assert.Equal(1, fixture.ProfileCalls);
        Assert.Equal(Subject, fixture.LookedUpSubject);
        Assert.Equal(module, fixture.RequestedModule);
    }

    [Fact]
    public async Task IncompleteProfile_IsNotAutomaticallyDenied()
    {
        var fixture = new Fixture();

        Assert.False(fixture.Profile!.IsComplete);

        Assert.True(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report()));
    }

    [Fact]
    public async Task UnauthenticatedUser_IsDenied()
    {
        var fixture = new Fixture { Authenticated = false };

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report()));

        Assert.Equal(0, fixture.AccountCalls);
        Assert.Equal(0, fixture.ProfileCalls);
    }

    [Fact]
    public async Task EmptySubject_IsDenied()
    {
        var fixture = new Fixture { UserSubject = Guid.Empty };

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report()));

        Assert.Equal(0, fixture.AccountCalls);
    }

    [Fact]
    public async Task UnconfirmedIdentity_IsDenied()
    {
        var fixture = new Fixture { Confirmed = false };

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report()));

        Assert.Equal(0, fixture.AccountCalls);
    }

    [Fact]
    public async Task EmptyVerifiedAccountId_IsDenied()
    {
        var fixture = new Fixture();

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            Guid.Empty, Report()));

        Assert.Equal(0, fixture.AccountCalls);
    }

    [Fact]
    public async Task MissingAccount_IsDenied()
    {
        var fixture = new Fixture();
        fixture.AccountLookup =
            (_, _) => Task.FromResult<Account?>(null);

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report()));

        Assert.Equal(0, fixture.ProfileCalls);
    }

    [Fact]
    public async Task DifferentInternalAccount_IsDenied()
    {
        var fixture = new Fixture();

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            Guid.NewGuid(), Report()));

        Assert.Equal(0, fixture.ProfileCalls);
    }

    [Fact]
    public async Task DifferentReporterProfile_IsDenied()
    {
        var fixture = new Fixture();

        fixture.Profile = new PrivateProfileDto(
            Guid.NewGuid(), "learning", Now);

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report()));
    }

    [Fact]
    public async Task CrossModuleProfile_IsDenied()
    {
        var fixture = new Fixture();

        fixture.Profile = new PrivateProfileDto(
            ReporterProfileId, "hobbies", Now);

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report("learning")));
    }

    [Fact]
    public async Task MissingProfile_IsDenied()
    {
        var fixture = new Fixture { Profile = null };

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report()));
    }

    [Fact]
    public async Task ProfileAccessDenial_ReturnsFalse()
    {
        var fixture = new Fixture();

        fixture.ProfileLookup = (_, _) =>
            Task.FromException<PrivateProfileDto>(
                new ProfileAccessDeniedException());

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report()));
    }

    [Fact]
    public async Task ProfileMissingException_ReturnsFalse()
    {
        var fixture = new Fixture();

        fixture.ProfileLookup = (_, _) =>
            Task.FromException<PrivateProfileDto>(
                new ProfileMissingException());

        Assert.False(await fixture.Reader.IsAuthorizedReporterAsync(
            fixture.StoredAccount.Id, Report()));
    }

    [Fact]
    public async Task AccountLookupTimeout_Propagates()
    {
        var fixture = new Fixture();

        fixture.AccountLookup = (_, _) =>
            Task.FromException<Account?>(
                new TimeoutException("Synthetic timeout"));

        await Assert.ThrowsAsync<TimeoutException>(
            () => fixture.Reader.IsAuthorizedReporterAsync(
                fixture.StoredAccount.Id, Report()));

        Assert.Equal(0, fixture.ProfileCalls);
    }

    [Fact]
    public async Task ProfileInfrastructureFailure_Propagates()
    {
        var fixture = new Fixture();

        fixture.ProfileLookup = (_, _) =>
            Task.FromException<PrivateProfileDto>(
                new InvalidOperationException(
                    "Synthetic repository failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Reader.IsAuthorizedReporterAsync(
                fixture.StoredAccount.Id, Report()));
    }

    [Fact]
    public async Task PreCancelledRequest_DoesNotReadAccount()
    {
        var fixture = new Fixture();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Reader.IsAuthorizedReporterAsync(
                fixture.StoredAccount.Id,
                Report(),
                cancellation.Token));

        Assert.Equal(0, fixture.AccountCalls);
    }

    [Fact]
    public async Task CancellationAfterAccountLookup_PreventsProfileRead()
    {
        var fixture = new Fixture();

        using var cancellation = new CancellationTokenSource();

        fixture.AccountLookup = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<Account?>(
                fixture.StoredAccount);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Reader.IsAuthorizedReporterAsync(
                fixture.StoredAccount.Id,
                Report(),
                cancellation.Token));

        Assert.Equal(0, fixture.ProfileCalls);
    }

    [Fact]
    public async Task CancellationAfterProfileRead_PreventsSuccess()
    {
        var fixture = new Fixture();

        using var cancellation = new CancellationTokenSource();

        fixture.ProfileLookup = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(fixture.Profile!);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Reader.IsAuthorizedReporterAsync(
                fixture.StoredAccount.Id,
                Report(),
                cancellation.Token));
    }

    private sealed class Fixture
    {
        public Account StoredAccount { get; } =
            Account.Create(Subject, Now);

        public bool Authenticated { get; set; } = true;
        public Guid? UserSubject { get; set; } = Subject;
        public bool Confirmed { get; set; } = true;

        public PrivateProfileDto? Profile { get; set; } =
            new(ReporterProfileId, "learning", Now);

        public int AccountCalls { get; private set; }
        public int ProfileCalls { get; private set; }

        public Guid LookedUpSubject { get; private set; }
        public string? RequestedModule { get; private set; }

        public Func<Guid, CancellationToken, Task<Account?>>?
            AccountLookup { get; set; }

        public Func<string, CancellationToken, Task<PrivateProfileDto>>?
            ProfileLookup { get; set; }

        public TrustedModerationReporterProfileAccessReader Reader
            { get; }

        public Fixture()
        {
            var user = Stub<ICurrentUser>((method, _) =>
                method switch
                {
                    "get_IsAuthenticated" => Authenticated,
                    "get_UserId" => UserSubject,
                    _ => throw new NotSupportedException(method)
                });

            var identity = Stub<IOnboardingIdentity>(
                (method, args) =>
                {
                    if (method != "IsConfirmedFor")
                        throw new NotSupportedException(method);

                    return Confirmed &&
                        UserSubject is Guid subject &&
                        subject == (Guid)args![0]!;
                });

            var accounts = Stub<IAccountRepository>(
                (method, args) =>
                {
                    if (method != "FindBySubjectAsync")
                        throw new NotSupportedException(method);

                    AccountCalls++;

                    var subject = (Guid)args![0]!;
                    var token = (CancellationToken)args[1]!;

                    LookedUpSubject = subject;

                    return AccountLookup?.Invoke(subject, token)
                        ?? Task.FromResult<Account?>(StoredAccount);
                });

            var profiles = Stub<IPrivateProfileService>(
                (method, args) =>
                {
                    if (method != "GetAsync")
                        throw new NotSupportedException(method);

                    ProfileCalls++;

                    var module = (string)args![0]!;
                    var token = (CancellationToken)args[1]!;

                    RequestedModule = module;

                    return ProfileLookup?.Invoke(module, token)
                        ?? Task.FromResult(Profile!);
                });

            Reader = new TrustedModerationReporterProfileAccessReader(
                user, identity, accounts, profiles);
        }
    }

    private static T Stub<T>(
        Func<string, object?[]?, object?> handler)
        where T : class
    {
        var stub = DispatchProxy.Create<T, MockProxy>();

        ((MockProxy)(object)stub).Handler = handler;

        return stub;
    }

    public class MockProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler
            { get; set; } = (_, _) =>
                throw new InvalidOperationException(
                    "Unconfigured mock invocation.");

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            return Handler(
                targetMethod?.Name
                    ?? throw new InvalidOperationException(),
                args);
        }
    }
}
