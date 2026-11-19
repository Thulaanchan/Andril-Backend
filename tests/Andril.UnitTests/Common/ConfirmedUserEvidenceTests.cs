using Andril.Common.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ConfirmedUserEvidenceTests
{
    [Fact]
    public void ExactSubjectNonAnonymousEmailAndConfirmationAreAllRequired()
    {
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var evidence = new ConfirmedUserEvidence(id, false, true, now.AddMinutes(-1));
        Assert.True(evidence.IsConfirmedFor(id, now));
        Assert.False(evidence.IsConfirmedFor(Guid.NewGuid(), now));
        Assert.False(evidence.IsConfirmedFor(Guid.Empty, now));
        Assert.False((evidence with { IsAnonymous = true }).IsConfirmedFor(id, now));
        Assert.False((evidence with { HasEmail = false }).IsConfirmedFor(id, now));
        Assert.False((evidence with { EmailConfirmedAt = null }).IsConfirmedFor(id, now));
        Assert.False((evidence with { EmailConfirmedAt = DateTimeOffset.MinValue }).IsConfirmedFor(id, now));
        Assert.False((evidence with { EmailConfirmedAt = now.AddSeconds(1) }).IsConfirmedFor(id, now));
        Assert.True((evidence with { EmailConfirmedAt = now.ToOffset(TimeSpan.FromHours(5.5)) }).IsConfirmedFor(id, now));
    }
}
