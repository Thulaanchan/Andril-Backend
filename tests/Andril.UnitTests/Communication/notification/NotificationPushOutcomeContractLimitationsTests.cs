using System;
using Andril.Communication.Interfaces;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// D16-B3 offline provider outcome contract characterization.
///
/// This suite documents a limitation of the current
/// result representation. It does not establish that
/// an external provider can supply an invalid value.
///
/// No provider, database or production dispatch is used.
/// </summary>
public sealed class NotificationPushOutcomeContractLimitationsTests
{
    [Fact]
    public void CurrentContract_DoesNotDefineExplicitUnknownOutcome()
    {
        var names = Enum.GetNames(
            typeof(NotificationPushOutcome));

        Assert.DoesNotContain("Unknown", names);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void UndefinedNumericOutcome_CanBePreservedByResult(
        int rawValue)
    {
        var outcome = (NotificationPushOutcome)rawValue;

        Assert.False(Enum.IsDefined(
            typeof(NotificationPushOutcome),
            outcome));

        var result = new NotificationPushResult(outcome);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(
            NotificationPushOutcomeClassification.Unknown,
            result.Classification);

        // Unknown does not satisfy the accepted-success predicate.
        Assert.False(result.WasAccepted);
    }
}
