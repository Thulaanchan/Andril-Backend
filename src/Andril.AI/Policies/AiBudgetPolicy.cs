using Andril.AI.Interfaces;
using Andril.AI.DTOs;
using System.Numerics;

namespace Andril.AI.Policies;

// Proposed internal evidence contracts. Values are meaningful only from independently trusted
// readers/storage. Records/IDs/flags are not authorization or proof of authenticity themselves.
// Complete coverage must be resolved from approved trusted pricing/billing records by M2.
// This marker is not approval or authentication. Unknown/default never authorizes paid spending.
public enum AiBillingCoverage { Unknown, Incomplete, ApprovedComplete }
/// <summary>Actual observations; optional categories remain missing, not verified zero.
/// Output already includes reasoning. No category prices or new formula are supplied.</summary>
public sealed record AiBillingUsage(long InputTokens, long OutputTokens, long TotalTokens,
    long? CachedInputTokens, long? ReasoningTokens, long? CacheWriteTokens,
    long? CacheWrite30MinuteTokens, long? CacheWrite12HourTokens)
{
    public override string ToString() => "AiBillingUsage [private evidence omitted]";
}
public enum AiExecutionMode { Unknown, PaidText, Mock }
public enum AiBillingState { Unknown, Reserved, Pending, Settled }
public enum AiBudgetOutcome { Unknown, Reserved, Existing, Status, Settled, Pending, Denied, Insufficient, Conflict, MockNoSpend }
public sealed record AiBudgetWindow(Guid Id, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public override string ToString() => "AiBudgetWindow [private evidence omitted]";
}
public sealed record AiPricingEvidence(string Model, string Revision, decimal InputUsdPerMillion,
    decimal OutputUsdPerMillion, bool SupportedTextOnly,
    AiBillingCoverage Coverage = AiBillingCoverage.Unknown)
{
    public override string ToString() => "AiPricingEvidence [private evidence omitted]";
}
public sealed record AiTokenEvidence(string Model, string RequestFingerprint, int SystemTokens, int UserTokens,
    int HistoryTokens, int FramingTokens, int OtherInputTokens, int FullInputTokens, int MaxBillableOutputTokens,
    bool FullRequestVerified, bool OutputIncludesReasoning)
{
    public override string ToString() => "AiTokenEvidence [private evidence omitted]";
}
public sealed record AiBudgetPreparation(AiGenerationAttemptReference Attempt, AiOperationSnapshot Operation,
    AiBudgetWindow Window, DateTimeOffset NowUtc, AiExecutionMode Mode, AiTokenEvidence? Tokens, AiPricingEvidence? Pricing,
    AiTokenCountResult? Measurement = null, AiProviderPayloadBinding? ExpectedPayloadBinding = null)
{
    public override string ToString() => "AiBudgetPreparation [private evidence omitted]";
}
public sealed record AiBudgetBalance(decimal CommittedUsd, decimal OutstandingUsd)
{
    public override string ToString() => "AiBudgetBalance [private evidence omitted]";
}
public sealed record AiBudgetReservation(AiGenerationAttemptReference Attempt, AiBudgetWindow Window,
    string Model, string PricingRevision, string RequestFingerprint, decimal ReservedUsd,
    AiBillingState State, decimal? ActualUsd = null, Guid? BillingEventId = null,
    AiProviderBillingEvidence? FinalEvidence = null, AiTokenCountResult? Measurement = null)
{
    public override string ToString() => "AiBudgetReservation [private evidence omitted]";
}
public sealed record AiBudgetResult(AiBudgetOutcome Outcome, AiBudgetReservation? Reservation = null)
{
    public override string ToString() => "AiBudgetResult [private evidence omitted]";
}
public enum AiBillingEvidenceKind { Unknown, Uncertain, Final }
/// <summary>Final means full cumulative liability for this execution is authoritative, including
/// billable reasoning. Input/output usage are ACTUAL usage, not requested token limits.
/// Verified invoice adjustments require a future versioned contract; conflicting events block.</summary>
public sealed record AiProviderBillingEvidence(Guid EventId, AiGenerationAttemptReference Attempt,
    Guid WindowId, string Model, string PricingRevision, AiBillingEvidenceKind Kind,
    long InputTokens, long BillableOutputTokens, decimal? ActualUsd, bool UsageVerified,
    AiBillingUsage? Usage = null, AiBillingCoverage Coverage = AiBillingCoverage.Unknown)
{
    public override string ToString() => "AiProviderBillingEvidence [private evidence omitted]";
}
public sealed record AiBudgetSettlement(AiBudgetOutcome Outcome, AiBudgetReservation Reservation,
    decimal AdditionalCommittedUsd, decimal ReleasedExposureUsd)
{
    public override string ToString() => "AiBudgetSettlement [private evidence omitted]";
}
public sealed record AiBudgetWarnings(bool First, bool Second, bool CapReached)
{
    public override string ToString() => "AiBudgetWarnings [private evidence omitted]";
}

/// <summary>Pure policy, never a durable balance or dispatch gate. Pricing rates are supplied
/// by trusted configuration, never assumed current. Monetary precision proposal: USD 0.000001;
/// round estimates UP, no cached discounts, zero additional margin only for verified full upper
/// bounds and supported text billing. M2 must review precision/margin and provider usage contract.</summary>
public static class AiBudgetPolicy
{
    public const decimal MonetaryQuantumUsd = 0.000001m;
    private const decimal Million = 1_000_000m;
    public static bool ValidWindow(AiBudgetWindow? window) => window is not null && window.Id != Guid.Empty &&
        AiOperationPolicy.ValidUtc(window.StartUtc) && AiOperationPolicy.ValidUtc(window.EndUtc) &&
        window.EndUtc - window.StartUtc == AiPrototypeLimits.Duration;
    public static bool WindowOpen(AiBudgetWindow? window, DateTimeOffset now) => ValidWindow(window) &&
        AiOperationPolicy.ValidUtc(now) && now >= window!.StartUtc && now < window.EndUtc;
    public static bool ValidAttempt(AiGenerationAttemptReference? attempt) => attempt is not null &&
        attempt.AttemptId != Guid.Empty && AiOperationPolicy.ValidReference(attempt.Operation);
    public static bool ValidPricing(AiPricingEvidence? pricing) => pricing is not null &&
        !string.IsNullOrWhiteSpace(pricing.Model) && !string.IsNullOrWhiteSpace(pricing.Revision) &&
        pricing.InputUsdPerMillion > 0 && pricing.OutputUsdPerMillion > 0 && pricing.SupportedTextOnly &&
        pricing.Coverage == AiBillingCoverage.ApprovedComplete;

    public static bool ValidTokens(AiTokenEvidence? tokens, string model) => tokens is not null &&
        tokens.Model == model && !string.IsNullOrWhiteSpace(tokens.RequestFingerprint) &&
        tokens.FullRequestVerified && tokens.OutputIncludesReasoning && tokens.SystemTokens >= 0 && tokens.UserTokens > 0 &&
        tokens.HistoryTokens >= 0 && tokens.FramingTokens > 0 && tokens.OtherInputTokens >= 0 &&
        tokens.FullInputTokens > 0 && tokens.FullInputTokens <= AiPrototypeLimits.MaxInputTokens &&
        (long)tokens.SystemTokens + tokens.UserTokens + tokens.HistoryTokens + tokens.FramingTokens + tokens.OtherInputTokens == tokens.FullInputTokens &&
        tokens.MaxBillableOutputTokens > 0 && tokens.MaxBillableOutputTokens <= AiPrototypeLimits.MaxOutputTokens;

    public static decimal Estimate(AiExecutionMode mode, AiTokenEvidence? tokens, AiPricingEvidence? pricing)
    {
        if (mode == AiExecutionMode.Mock) return 0m; // Mock never allocates a paid reservation.
        if (mode != AiExecutionMode.PaidText || !ValidPricing(pricing) || !ValidTokens(tokens, pricing!.Model)) throw Invalid();
        try
        {
            // Exact scaled-decimal intermediates prevent decimal multiplication from rounding
            // away a tiny positive fraction at a micro-USD boundary. No floating point.
            var (input, inputScale) = DecimalParts(pricing!.InputUsdPerMillion);
            var (output, outputScale) = DecimalParts(pricing.OutputUsdPerMillion);
            var scale = Math.Max(inputScale, outputScale);
            var denominator = BigInteger.Pow(10, scale);
            var numerator = tokens!.FullInputTokens * input * BigInteger.Pow(10, scale - inputScale) +
                tokens.MaxBillableOutputTokens * output * BigInteger.Pow(10, scale - outputScale);
            var microUsd = BigInteger.DivRem(numerator, denominator, out var remainder);
            if (remainder > 0) microUsd++;
            var estimate = checked((decimal)microUsd) / Million;
            if (estimate <= 0) throw Invalid();
            return estimate;
        }
        catch (OverflowException) { throw Invalid(); }
    }

    public static decimal RoundUp(decimal amount)
    {
        if (amount < 0) throw Invalid();
        try { return checked(decimal.Ceiling(amount / MonetaryQuantumUsd) * MonetaryQuantumUsd); }
        catch (OverflowException) { throw Invalid(); }
    }

    private static (BigInteger Coefficient, int Scale) DecimalParts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        return (coefficient, (bits[3] >> 16) & 255);
    }

    public static bool ValidBalance(AiBudgetBalance? balance) => balance is not null && balance.CommittedUsd >= 0 &&
        balance.OutstandingUsd >= 0 && balance.CommittedUsd <= decimal.MaxValue - balance.OutstandingUsd;
    public static decimal Available(AiBudgetBalance balance)
    {
        if (!ValidBalance(balance)) throw Invalid();
        return AiPrototypeLimits.BudgetUsd - (balance.CommittedUsd + balance.OutstandingUsd);
    }
    public static bool CanReserve(AiBudgetBalance? balance, decimal exposure, AiBudgetWindow? window, DateTimeOffset now) =>
        ValidBalance(balance) && WindowOpen(window, now) && exposure > 0 && exposure <= AiPrototypeLimits.BudgetUsd &&
        Available(balance!) >= exposure;

    public static void ValidatePreparation(AiBudgetPreparation? evidence, AiGenerationAttemptReference expected)
    {
        if (evidence is null || !ValidAttempt(expected) || evidence.Attempt != expected ||
            !AiOperationPolicy.ValidSnapshot(evidence.Operation) || evidence.Operation.Operation != expected.Operation ||
            evidence.Operation.Status != AiOperationStatus.Reserved || evidence.Operation.AttemptId != expected.AttemptId ||
            !WindowOpen(evidence.Window, evidence.NowUtc) || !Enum.IsDefined(evidence.Mode) || evidence.Mode == AiExecutionMode.Unknown)
            throw Invalid();
        if (evidence.Mode == AiExecutionMode.PaidText &&
            (!AiTokenCountContract.MatchesBinding(evidence.Measurement, evidence.ExpectedPayloadBinding) ||
             evidence.Measurement!.Evidence != evidence.Tokens)) throw Invalid();
        Estimate(evidence.Mode, evidence.Tokens, evidence.Pricing);
    }

    public static bool ValidReservation(AiBudgetReservation? reservation)
    {
        if (reservation is null || !ValidAttempt(reservation.Attempt) || !ValidWindow(reservation.Window) ||
            string.IsNullOrWhiteSpace(reservation.Model) || string.IsNullOrWhiteSpace(reservation.PricingRevision) ||
            string.IsNullOrWhiteSpace(reservation.RequestFingerprint) || reservation.ReservedUsd <= 0 ||
            reservation.ReservedUsd > AiPrototypeLimits.BudgetUsd || !Enum.IsDefined(reservation.State) ||
            !AiTokenCountContract.MatchesBinding(reservation.Measurement, reservation.Measurement?.PayloadBinding) ||
            reservation.Measurement!.Evidence!.Model != reservation.Model ||
            reservation.Measurement.Evidence.RequestFingerprint != reservation.RequestFingerprint) return false;
        return reservation.State switch
        {
            AiBillingState.Reserved or AiBillingState.Pending => reservation.ActualUsd is null && reservation.BillingEventId is null && reservation.FinalEvidence is null,
            AiBillingState.Settled => reservation.ActualUsd is >= 0 && ValidBilling(reservation.FinalEvidence) &&
                reservation.FinalEvidence!.Kind == AiBillingEvidenceKind.Final && reservation.FinalEvidence.Attempt == reservation.Attempt &&
                reservation.FinalEvidence.WindowId == reservation.Window.Id && reservation.FinalEvidence.Model == reservation.Model &&
                reservation.FinalEvidence.PricingRevision == reservation.PricingRevision &&
                reservation.FinalEvidence.ActualUsd == reservation.ActualUsd && reservation.FinalEvidence.EventId == reservation.BillingEventId,
            _ => false
        };
    }

    /// <summary>Compare only after authorized lookup. Does not create a second reservation.
    /// A settled/pending replay remains settled/pending, never new dispatch permission.</summary>
    public static bool SameReservation(AiBudgetReservation stored, AiBudgetPreparation prepared) =>
        ValidReservation(stored) && stored.Attempt == prepared.Attempt && stored.Window == prepared.Window &&
        stored.Model == prepared.Pricing?.Model && stored.PricingRevision == prepared.Pricing?.Revision &&
        stored.RequestFingerprint == prepared.Tokens?.RequestFingerprint &&
        stored.Measurement == prepared.Measurement &&
        stored.Measurement?.PayloadBinding == prepared.ExpectedPayloadBinding &&
        stored.ReservedUsd == Estimate(prepared.Mode, prepared.Tokens, prepared.Pricing);

    public static bool ValidBilling(AiProviderBillingEvidence? report) => report is not null && report.EventId != Guid.Empty &&
        ValidAttempt(report.Attempt) && report.WindowId != Guid.Empty && !string.IsNullOrWhiteSpace(report.Model) &&
        !string.IsNullOrWhiteSpace(report.PricingRevision) && report.InputTokens >= 0 && report.BillableOutputTokens >= 0 &&
        report.Kind switch
        {
            AiBillingEvidenceKind.Uncertain => report.ActualUsd is null,
            AiBillingEvidenceKind.Final => report.UsageVerified && report.ActualUsd is >= 0 &&
                report.Coverage == AiBillingCoverage.ApprovedComplete && ValidUsage(report.Usage) &&
                report.Usage!.InputTokens == report.InputTokens && report.Usage.OutputTokens == report.BillableOutputTokens,
            _ => false
        };

    public static bool ValidUsage(AiBillingUsage? usage) => usage is not null &&
        usage.InputTokens >= 0 && usage.OutputTokens >= 0 && usage.InputTokens <= long.MaxValue - usage.OutputTokens &&
        usage.TotalTokens == usage.InputTokens + usage.OutputTokens &&
        (usage.CachedInputTokens is null || usage.CachedInputTokens >= 0 && usage.CachedInputTokens <= usage.InputTokens) &&
        (usage.ReasoningTokens is null || usage.ReasoningTokens >= 0 && usage.ReasoningTokens <= usage.OutputTokens) &&
        (usage.CacheWriteTokens is null || usage.CacheWriteTokens >= 0 && usage.CacheWriteTokens <= usage.InputTokens) &&
        (usage.CachedInputTokens is null || usage.CacheWriteTokens is null ||
            usage.CachedInputTokens <= usage.InputTokens - usage.CacheWriteTokens) &&
        ((usage.CacheWrite30MinuteTokens is null && usage.CacheWrite12HourTokens is null) ||
         (usage.CacheWrite30MinuteTokens is >= 0 && usage.CacheWrite12HourTokens is >= 0 &&
          usage.CacheWrite30MinuteTokens <= long.MaxValue - usage.CacheWrite12HourTokens &&
          usage.CacheWriteTokens == usage.CacheWrite30MinuteTokens + usage.CacheWrite12HourTokens));

    /// <summary>Final authoritative actual USD is not clamped to reservation or cap. A finalized
    /// cumulative zero/partial bill may release exposure; ambiguous billing keeps it all held.
    /// Duplicate/conflict detection is per execution as well as event ID, not only per report.</summary>
    public static AiBudgetSettlement Settle(AiBudgetReservation reservation, AiProviderBillingEvidence report)
    {
        if (!ValidReservation(reservation) || !ValidBilling(report) || reservation.Attempt != report.Attempt ||
            reservation.Window.Id != report.WindowId || reservation.Model != report.Model || reservation.PricingRevision != report.PricingRevision)
            throw Invalid();
        if (reservation.State == AiBillingState.Settled)
        {
            var identical = reservation.FinalEvidence == report;
            return new(identical ? AiBudgetOutcome.Existing : AiBudgetOutcome.Conflict, reservation, 0, 0);
        }
        if (report.Kind == AiBillingEvidenceKind.Uncertain)
            return new(AiBudgetOutcome.Pending, reservation with { State = AiBillingState.Pending }, 0, 0);
        var actual = report.ActualUsd!.Value;
        return new(AiBudgetOutcome.Settled, reservation with { State = AiBillingState.Settled, ActualUsd = actual, BillingEventId = report.EventId, FinalEvidence = report },
            actual, Math.Max(0, reservation.ReservedUsd - actual));
    }

    /// <summary>Proposed warning basis: committed + outstanding. Pure upward crossings;
    /// decreases rearm thresholds. Durable deduplication/rearming/delivery require review.</summary>
    public static AiBudgetWarnings Crossings(AiBudgetBalance before, AiBudgetBalance after)
    {
        if (!ValidBalance(before) || !ValidBalance(after)) throw Invalid();
        var previous = before.CommittedUsd + before.OutstandingUsd;
        var current = after.CommittedUsd + after.OutstandingUsd;
        return new(previous < AiPrototypeLimits.FirstAlertUsd && current >= AiPrototypeLimits.FirstAlertUsd,
            previous < AiPrototypeLimits.SecondAlertUsd && current >= AiPrototypeLimits.SecondAlertUsd,
            previous < AiPrototypeLimits.BudgetUsd && current >= AiPrototypeLimits.BudgetUsd);
    }
    private static InvalidOperationException Invalid() => new("AI budget evidence could not be verified.");
}
