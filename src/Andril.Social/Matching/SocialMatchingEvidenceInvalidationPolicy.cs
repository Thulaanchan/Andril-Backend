using System;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Granular reasons why a previously cached or evaluated matching evidence snapshot is no longer current.
/// </summary>
[Flags]
public enum SocialEvidenceInvalidationReason
{
    None = 0,
    QuestionnaireRevisionChanged = 1 << 0,
    QuestionnaireWithdrawn = 1 << 1,
    ArchetypeConfirmationChanged = 1 << 2,
    PartnerPreferencesChanged = 1 << 3,
    DemographicsChanged = 1 << 4,
    LocationDistanceChanged = 1 << 5,
    LanguagesChanged = 1 << 6,
    ConnectionGoalsChanged = 1 << 7,
    SocialInterestsChanged = 1 << 8,
    BlockingChanged = 1 << 9,
    UnmatchingChanged = 1 << 10,
    ProfileDeactivated = 1 << 11,
    AccountRestricted = 1 << 12,
    PolicyVersionChanged = 1 << 13
}

/// <summary>
/// Status of matching evidence currency evaluation.
/// </summary>
public enum SocialEvidenceCurrencyStatus
{
    /// <summary>Evidence snapshot is identical to latest committed profile state.</summary>
    Current,

    /// <summary>Evidence is stale and requires on-demand re-evaluation.</summary>
    StaleRequiresRefresh,

    /// <summary>Profile is deactivated or account is restricted (fails closed).</summary>
    RevokedOrDeactivated,

    /// <summary>The engine scoring policy has been updated, requiring fresh calculation.</summary>
    IncompatiblePolicyVersion
}

/// <summary>
/// Guard enum for forbidden side-effects that must never be triggered by score calculations or updates.
/// </summary>
public enum SocialScoreChangeProhibitedEffect
{
    CreateLike,
    RemoveLike,
    CreateMatch,
    RemoveMatch,
    AuthorizeCommunication
}

/// <summary>
/// Result of evaluating matching evidence currency against latest committed profile state.
/// </summary>
public sealed class SocialEvidenceCurrencyResult : IEquatable<SocialEvidenceCurrencyResult>
{
    [JsonIgnore]
    public SocialEvidenceCurrencyStatus Status { get; }

    [JsonIgnore]
    public SocialEvidenceInvalidationReason InvalidationReasons { get; }

    [JsonIgnore]
    public bool IsCurrent => Status == SocialEvidenceCurrencyStatus.Current;

    public SocialEvidenceCurrencyResult(
        SocialEvidenceCurrencyStatus status,
        SocialEvidenceInvalidationReason invalidationReasons)
    {
        Status = status;
        InvalidationReasons = invalidationReasons;
    }

    public static SocialEvidenceCurrencyResult ValidCurrent() =>
        new(SocialEvidenceCurrencyStatus.Current, SocialEvidenceInvalidationReason.None);

    public static SocialEvidenceCurrencyResult Stale(SocialEvidenceInvalidationReason reasons) =>
        new(SocialEvidenceCurrencyStatus.StaleRequiresRefresh, reasons);

    public static SocialEvidenceCurrencyResult Revoked(SocialEvidenceInvalidationReason reasons) =>
        new(SocialEvidenceCurrencyStatus.RevokedOrDeactivated, reasons);

    public static SocialEvidenceCurrencyResult PolicyChanged() =>
        new(SocialEvidenceCurrencyStatus.IncompatiblePolicyVersion, SocialEvidenceInvalidationReason.PolicyVersionChanged);

    public bool Equals(SocialEvidenceCurrencyResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Status == other.Status && InvalidationReasons == other.InvalidationReasons;
    }

    public override bool Equals(object? obj) => obj is SocialEvidenceCurrencyResult other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Status, InvalidationReasons);

    public override string ToString() => "SocialEvidenceCurrencyResult [private]";
}

/// <summary>
/// Pure, deterministic domain policy governing Social matching evidence currency, invalidation,
/// and non-side-effect guarantees (P-04 / Task 152).
/// </summary>
public static class SocialMatchingEvidenceInvalidationPolicy
{
    /// <summary>
    /// Evaluates whether an existing evidence snapshot remains current against the latest committed state.
    /// Returns <see cref="SocialEvidenceCurrencyStatus.Current"/> only when all revisions and policy versions match.
    /// </summary>
    public static SocialEvidenceCurrencyResult EvaluateCurrency(
        SocialEvidenceRevisionSnapshot cachedSnapshot,
        SocialEvidenceRevisionSnapshot latestCommittedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(cachedSnapshot);
        ArgumentNullException.ThrowIfNull(latestCommittedSnapshot);

        if (cachedSnapshot.ProfileId != latestCommittedSnapshot.ProfileId)
        {
            throw new ArgumentException(
                "Cannot evaluate currency between snapshots of different profile identities.",
                nameof(latestCommittedSnapshot));
        }

        // 1. Account restriction or profile deactivation fails closed immediately
        if (latestCommittedSnapshot.IsAccountRestricted)
        {
            return SocialEvidenceCurrencyResult.Revoked(SocialEvidenceInvalidationReason.AccountRestricted);
        }

        if (!latestCommittedSnapshot.IsProfileActive)
        {
            return SocialEvidenceCurrencyResult.Revoked(SocialEvidenceInvalidationReason.ProfileDeactivated);
        }

        // 2. Scoring policy update invalidates evidence
        if (!string.Equals(cachedSnapshot.PolicyVersion, latestCommittedSnapshot.PolicyVersion, StringComparison.Ordinal))
        {
            return SocialEvidenceCurrencyResult.PolicyChanged();
        }

        // 3. Accumulate revision invalidation reasons
        var reasons = SocialEvidenceInvalidationReason.None;

        if (cachedSnapshot.QuestionnaireRevision != latestCommittedSnapshot.QuestionnaireRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.QuestionnaireRevisionChanged;
        }

        if (!cachedSnapshot.IsQuestionnaireWithdrawn && latestCommittedSnapshot.IsQuestionnaireWithdrawn)
        {
            reasons |= SocialEvidenceInvalidationReason.QuestionnaireWithdrawn;
        }

        if (cachedSnapshot.ArchetypeConfirmationRevision != latestCommittedSnapshot.ArchetypeConfirmationRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.ArchetypeConfirmationChanged;
        }

        if (cachedSnapshot.PartnerPreferencesRevision != latestCommittedSnapshot.PartnerPreferencesRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.PartnerPreferencesChanged;
        }

        if (cachedSnapshot.DemographicsRevision != latestCommittedSnapshot.DemographicsRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.DemographicsChanged;
        }

        if (cachedSnapshot.LocationDistanceRevision != latestCommittedSnapshot.LocationDistanceRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.LocationDistanceChanged;
        }

        if (cachedSnapshot.LanguagesRevision != latestCommittedSnapshot.LanguagesRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.LanguagesChanged;
        }

        if (cachedSnapshot.ConnectionGoalsRevision != latestCommittedSnapshot.ConnectionGoalsRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.ConnectionGoalsChanged;
        }

        if (cachedSnapshot.SocialInterestsRevision != latestCommittedSnapshot.SocialInterestsRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.SocialInterestsChanged;
        }

        if (cachedSnapshot.BlockingRevision != latestCommittedSnapshot.BlockingRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.BlockingChanged;
        }

        if (cachedSnapshot.UnmatchingRevision != latestCommittedSnapshot.UnmatchingRevision)
        {
            reasons |= SocialEvidenceInvalidationReason.UnmatchingChanged;
        }

        if (reasons != SocialEvidenceInvalidationReason.None)
        {
            return SocialEvidenceCurrencyResult.Stale(reasons);
        }

        return SocialEvidenceCurrencyResult.ValidCurrent();
    }

    /// <summary>
    /// Enforces that uncommitted or aborted updates do not invalidate existing valid committed evidence.
    /// </summary>
    public static bool ShouldInvalidateOnTransactionOutcome(bool isTransactionCommitted) =>
        isTransactionCommitted;

    /// <summary>
    /// Guard enforcing the architectural rule that score changes must never automatically trigger
    /// mutations to likes, matches, or communication authorization.
    /// </summary>
    public static void AssertNoSideEffect(SocialScoreChangeProhibitedEffect attemptedEffect)
    {
        throw new InvalidOperationException(
            $"Score calculation or refresh is strictly non-side-effecting and is prohibited from triggering '{attemptedEffect}'.");
    }
}
