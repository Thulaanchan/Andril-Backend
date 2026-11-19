using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class AccountEligibilityTests
{
    [Theory]
    [InlineData("2010-11-15", "2026-11-14", 15)]
    [InlineData("2010-11-15", "2026-11-15", 16)]
    [InlineData("2010-11-15", "2026-11-16", 16)]
    [InlineData("2008-11-15", "2026-11-14", 17)]
    [InlineData("2008-11-15", "2026-11-15", 18)]
    [InlineData("2008-11-15", "2026-11-16", 18)]
    [InlineData("2008-02-29", "2024-02-28", 15)]
    [InlineData("2008-02-29", "2024-02-29", 16)]
    [InlineData("2008-02-29", "2026-02-28", 17)]
    [InlineData("2008-02-29", "2026-03-01", 18)]
    [InlineData("2084-02-29", "2100-02-28", 15)]
    [InlineData("2084-02-29", "2100-03-01", 16)]
    [InlineData("2010-12-31", "2026-01-01", 15)]
    [InlineData("2010-01-01", "2026-01-01", 16)]
    [InlineData("2026-11-15", "2026-11-15", 0)]
    public void CalendarBoundariesAndAllThreeModules(string birth, string today, int expected)
    {
        var dob = DateOnly.ParseExact(birth, "yyyy-MM-dd");
        var date = DateOnly.ParseExact(today, "yyyy-MM-dd");
        Assert.Equal(expected, AgeEligibilityPolicy.CalculateAge(dob, date));
        Assert.Equal(new(expected >= 16, expected >= 16, expected >= 18), AgeEligibilityPolicy.Evaluate(dob, date));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("\u00a0")]
    [InlineData("value\n")]
    public void EmptyOrControlGenderIsRejected(string? gender) =>
        Assert.Throws<EligibilityValidationException>(() => GenderInputPolicy.Normalize(gender));

    [Fact]
    public void GenderVocabularyIsNotInvented_AndTechnicalLimitIsEnforced()
    {
        Assert.Equal("self-described value", GenderInputPolicy.Normalize("  self-described value  "));
        Assert.Equal("சுய விளக்கம்", GenderInputPolicy.Normalize("சுய விளக்கம்"));
        Assert.Throws<EligibilityValidationException>(() => GenderInputPolicy.Normalize(new string('x', 101)));
    }

    [Fact]
    public async Task MissingAndFutureDobFailWithoutWriting()
    {
        var store = new Store();
        var service = Service(store);
        await Assert.ThrowsAsync<EligibilityValidationException>(() => service.SubmitAsync(null, "value"));
        await Assert.ThrowsAsync<EligibilityValidationException>(() => service.SubmitAsync(DateOnly.MinValue, "value"));
        await Assert.ThrowsAsync<EligibilityValidationException>(() => service.SubmitAsync(new(2026, 11, 16), "value"));
        Assert.Null(store.Details);
    }

    [Fact]
    public async Task UtcDateAndBirthdayRecalculation_DoNotDependOnHostTimezoneOrStoredFlags()
    {
        var store = new Store();
        // Local date is Nov 15, but UTC date is still Nov 14.
        var clock = new Clock(new(2026, 11, 15, 0, 0, 0, TimeSpan.FromHours(5.5)));
        var service = Service(store, clock);
        var result = await service.SubmitAsync(new(2010, 11, 15), "value");
        Assert.Equal(new(2026, 11, 14), result.EvaluatedOnUtc);
        Assert.False(result.AgeEligibility.Learning);
        clock.Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var birthday = await service.GetAsync();
        Assert.True(birthday.AgeEligibility.Learning);
        Assert.False(birthday.AgeEligibility.Social);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task AbsentDetailsFailClosed_AndRequireExistingAuthenticatedAccount()
    {
        var store = new Store();
        var result = await Service(store).GetAsync();
        Assert.False(result.HasSubmission);
        Assert.Equal(new(false, false, false), result.AgeEligibility);
        var unauthenticated = new AccountEligibilityService(new User(store.Subject, false), store, store, new Clock());
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => unauthenticated.GetAsync());
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => unauthenticated.SubmitAsync(new(2000, 1, 1), "value"));
        store.Exists = false;
        await Assert.ThrowsAsync<EligibilityAccountMissingException>(() => Service(store).GetAsync());
        await Assert.ThrowsAsync<EligibilityAccountMissingException>(() => Service(store).SubmitAsync(new(2000, 1, 1), "value"));
        Assert.Equal(0, store.Writes);
    }

    private static AccountEligibilityService Service(Store store, Clock? clock = null) =>
        new(new User(store.Subject), store, store, clock ?? new Clock());
    private sealed record User(Guid? UserId, bool IsAuthenticated = true) : ICurrentUser;
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now;
        public Clock() : this(new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero)) { }
        public Clock(DateTimeOffset now) { Now = now; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : IAccountRepository, IAccountDemographicsRepository
    {
        internal Guid Subject = Guid.NewGuid();
        internal Account Account;
        internal AccountDemographics? Details;
        internal bool Exists = true;
        internal int Writes;
        internal Store() { Account = Account.Create(Subject, DateTimeOffset.UtcNow); }
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(Exists && subject == Subject ? Account : null);
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No implicit account creation.");
        public Task<AccountDemographics?> FindAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            return Task.FromResult(Details);
        }
        public Task<AccountDemographics> SaveAsync(Guid accountId, DateOnly dateOfBirth, string gender, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            Writes++;
            Details = AccountDemographics.Create(accountId, dateOfBirth, gender, now);
            return Task.FromResult(Details);
        }
    }
}
