using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Interfaces;

namespace Andril.Social.Matching;

/// <summary>
/// Status of an offline pair-interest observation operation.
/// </summary>
public enum SocialPairInterestObservationStatus
{
    /// <summary>Observation succeeded and repository evidence was structurally valid.</summary>
    Success,

    /// <summary>Repository returned evidence belonging to an unrelated pair or mismatched actor.</summary>
    ScopeViolation,

    /// <summary>Supplied profile pair key or acting profile identifier was invalid.</summary>
    InvalidIdentity
}

/// <summary>
/// Immutable, privacy-safe result of observing reciprocal interest for a canonical Social profile pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>Privacy and Security Safeguards:</b><br />
/// This result conceals individual directional decisions to prevent private interest leakage.
/// It does NOT constitute chat/communication authorization (e.g. M4 authorization evidence),
/// does NOT authenticate participants, and does NOT persist a mutual match entity.
/// </para>
/// </remarks>
public sealed class SocialPairInterestObservationResult : IEquatable<SocialPairInterestObservationResult>
{
    /// <summary>Gets the canonical profile pair key.</summary>
    public SocialProfilePairKey? PairKey { get; }

    /// <summary>Gets the evaluated reciprocal interest status.</summary>
    public SocialReciprocalInterestStatus ReciprocalStatus { get; }

    /// <summary>Gets the observation operation status.</summary>
    public SocialPairInterestObservationStatus ObservationStatus { get; }

    /// <summary>Gets a value indicating whether the observation succeeded without scope violations.</summary>
    public bool IsValid => ObservationStatus == SocialPairInterestObservationStatus.Success;

    /// <summary>Gets a value indicating whether both participants have confirmed mutual interest.</summary>
    public bool IsMutualMatch => IsValid && ReciprocalStatus == SocialReciprocalInterestStatus.MutualInterestObserved;

    private SocialPairInterestObservationResult(
        SocialProfilePairKey? pairKey,
        SocialReciprocalInterestStatus reciprocalStatus,
        SocialPairInterestObservationStatus observationStatus)
    {
        PairKey = pairKey;
        ReciprocalStatus = reciprocalStatus;
        ObservationStatus = observationStatus;
    }

    /// <summary>Creates a successful observation result.</summary>
    public static SocialPairInterestObservationResult Success(
        SocialProfilePairKey pairKey,
        SocialReciprocalInterestStatus reciprocalStatus) =>
        new(pairKey, reciprocalStatus, SocialPairInterestObservationStatus.Success);

    /// <summary>Creates a failed observation result due to scope violation from repository records.</summary>
    public static SocialPairInterestObservationResult ScopeViolation(SocialProfilePairKey pairKey) =>
        new(pairKey, SocialReciprocalInterestStatus.Indeterminate, SocialPairInterestObservationStatus.ScopeViolation);

    /// <summary>Creates a failed observation result due to invalid identity.</summary>
    public static SocialPairInterestObservationResult InvalidIdentity() =>
        new(null, SocialReciprocalInterestStatus.Indeterminate, SocialPairInterestObservationStatus.InvalidIdentity);

    public bool Equals(SocialPairInterestObservationResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Equals(PairKey, other.PairKey)
            && ReciprocalStatus == other.ReciprocalStatus
            && ObservationStatus == other.ObservationStatus;
    }

    public override bool Equals(object? obj) =>
        obj is SocialPairInterestObservationResult other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(PairKey, ReciprocalStatus, ObservationStatus);

    public override string ToString() =>
        "SocialPairInterestObservationResult [private]";
}

/// <summary>
/// Internal offline coordinator for consuming pair-scoped interest evidence from persistence (CF-20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// This coordinator performs offline evidence aggregation and validation only. It does NOT:
/// <list type="bullet">
/// <item><description>Grant communication or messaging access (e.g. M4 authorization).</description></item>
/// <item><description>Persist match records or alter matching state machines.</description></item>
/// <item><description>Expose another participant's private directional interest decision.</description></item>
/// <item><description>Authenticate caller sessions or verify Dating module opt-in or age standing.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SocialPairInterestObservationService
{
    private readonly ISocialInterestRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialPairInterestObservationService"/> class.
    /// </summary>
    /// <param name="repository">The scoped Social interest persistence repository.</param>
    public SocialPairInterestObservationService(ISocialInterestRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Asynchronously observes reciprocal interest for the specified canonical profile pair.
    /// Fails closed if repository records violate pair or actor scope boundaries.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A deterministic, privacy-safe <see cref="SocialPairInterestObservationResult"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pairKey"/> is null.</exception>
    public async Task<SocialPairInterestObservationResult> ObservePairInterestAsync(
        SocialProfilePairKey pairKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairKey);

        cancellationToken.ThrowIfCancellationRequested();

        var (firstInterest, secondInterest) = await _repository.GetPairInterestsAsync(pairKey, cancellationToken);

        // Validate first interest record if present
        if (firstInterest is not null)
        {
            var firstScopeValidation = SocialInterestResultScopePolicy.Validate(pairKey, pairKey.FirstProfileId, firstInterest);
            if (!firstScopeValidation.IsValid || !firstScopeValidation.HasDecision)
            {
                return SocialPairInterestObservationResult.ScopeViolation(pairKey);
            }
        }

        // Validate second interest record if present
        if (secondInterest is not null)
        {
            var secondScopeValidation = SocialInterestResultScopePolicy.Validate(pairKey, pairKey.SecondProfileId, secondInterest);
            if (!secondScopeValidation.IsValid || !secondScopeValidation.HasDecision)
            {
                return SocialPairInterestObservationResult.ScopeViolation(pairKey);
            }
        }

        var reciprocalStatus = SocialReciprocalInterestEvaluator.Evaluate(firstInterest, secondInterest);
        return SocialPairInterestObservationResult.Success(pairKey, reciprocalStatus);
    }
}
