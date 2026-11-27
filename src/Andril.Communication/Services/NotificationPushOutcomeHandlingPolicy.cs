using System;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Services;

internal sealed record NotificationPushOutcomeHandlingDecision(
    NotificationPushOutcomeClassification Classification,
    bool RequiresReconciliation,
    bool AutomaticRetryAllowed,
    bool DeviceDeliveryConfirmed);

internal static class NotificationPushOutcomeHandlingPolicy
{
    internal static NotificationPushOutcomeHandlingDecision Evaluate(
        NotificationPushResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var classification = result.Classification;
        return new NotificationPushOutcomeHandlingDecision(
            classification,
            classification == NotificationPushOutcomeClassification.Unknown,
            AutomaticRetryAllowed: false,
            DeviceDeliveryConfirmed: false);
    }
}
