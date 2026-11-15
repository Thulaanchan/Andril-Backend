using Andril.Common.Accounts;
using Andril.Common.Accounts.Policies;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class AccountPolicyTests
{
    [Fact]
    public void ReceiptUsesUtc_AndCannotFabricateOtherActionTypes()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(5.5));
        var record = AccountPolicyRecord.Create(Guid.NewGuid(), PolicyActions.TermsAgreed, "fixture-version", now);
        Assert.Equal(TimeSpan.Zero, record.RecordedAtUtc.Offset);
        Assert.Equal(now.ToUniversalTime(), record.RecordedAtUtc);
        Assert.Throws<PolicyValidationException>(() => AccountPolicyRecord.Create(Guid.NewGuid(), "community_agreed", "fixture", now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" version")]
    [InlineData("version\n")]
    public void InvalidVersionsAreRejected(string? version) =>
        Assert.Throws<PolicyValidationException>(() => AccountPolicyRecord.Create(Guid.NewGuid(), PolicyActions.TermsAgreed, version, DateTimeOffset.UtcNow));

    [Fact]
    public async Task ServiceRejectsUnauthenticatedCallsBeforeAnyDependencyAccess()
    {
        var service = new AccountPolicyService(new User(), null!, null!, null!, TimeProvider.System);
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => service.GetAsync());
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => service.AcknowledgeAsync(null));
    }
    private sealed record User : ICurrentUser
    {
        public Guid? UserId => null;
        public bool IsAuthenticated => false;
    }
}
