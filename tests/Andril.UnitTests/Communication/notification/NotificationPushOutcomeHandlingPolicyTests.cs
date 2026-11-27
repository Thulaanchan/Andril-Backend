using System;
using Andril.Communication.Interfaces;
using Andril.Communication.Services;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// Exercises the existing conservative outcome policy directly.
/// These decisions do not implement dispatch, retry, reconciliation,
/// or confirmation of delivery by a provider or device.
/// </summary>
public sealed class NotificationPushOutcomeHandlingPolicyTests
{
    [Theory]
    [InlineData(NotificationPushOutcome.NotSent,
        NotificationPushOutcomeClassification.NotSent)]
    [InlineData(NotificationPushOutcome.ProviderAccepted,
        NotificationPushOutcomeClassification.ProviderAccepted)]
    [InlineData(NotificationPushOutcome.TemporaryFailure,
        NotificationPushOutcomeClassification.TemporaryFailure)]
    [InlineData(NotificationPushOutcome.PermanentFailure,
        NotificationPushOutcomeClassification.ProviderRejected)]
    public void DefinedOutcomes_PreserveClassificationWithoutAuthorizingRetryOrDelivery(
        NotificationPushOutcome outcome,
        NotificationPushOutcomeClassification expectedClassification)
    {
        var result = new NotificationPushResult(outcome);

        var decision = NotificationPushOutcomeHandlingPolicy.Evaluate(result);

        Assert.Equal(expectedClassification, result.Classification);
        Assert.Equal(result.Classification, decision.Classification);
        Assert.False(decision.RequiresReconciliation);
        Assert.False(decision.AutomaticRetryAllowed);
        Assert.False(decision.DeviceDeliveryConfirmed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UndefinedOutcomes_RequireReconciliationWithoutAuthorizingRetryOrDelivery(
        int rawValue)
    {
        var outcome = (NotificationPushOutcome)rawValue;
        Assert.False(Enum.IsDefined(outcome));
        var result = new NotificationPushResult(outcome);

        var decision = NotificationPushOutcomeHandlingPolicy.Evaluate(result);

        Assert.Equal(NotificationPushOutcomeClassification.Unknown,
            result.Classification);
        Assert.Equal(result.Classification, decision.Classification);
        Assert.True(decision.RequiresReconciliation);
        Assert.False(decision.AutomaticRetryAllowed);
        Assert.False(decision.DeviceDeliveryConfirmed);
    }

    [Fact]
    public void NullResult_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => NotificationPushOutcomeHandlingPolicy.Evaluate(null!));

        Assert.Equal("result", exception.ParamName);
    }
}
