using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Status outcome of the offline Social discovery eligibility and privacy pre-filter evaluation.
/// </summary>
public enum SocialDiscoveryEligibilityStatus
{
    /// <summary>
    /// Candidate satisfies all baseline age, opt-in, activity, visibility, and bidirectional block-clearance criteria.
    /// </summary>
    Eligible,

    /// <summary>
    /// Candidate explicitly fails one or more confirmed baseline eligibility or privacy rules.
    /// </summary>
    Ineligible,

    /// <summary>
    /// Authoritative evidence is missing, unconfirmed, or indeterminate (fails closed).
    /// </summary>
    Indeterminate
}

/// <summary>
/// Detailed reason flags for candidate discovery ineligibility or indeterminate state.
/// </summary>
[Flags]
public enum SocialDiscoveryIneligibilityReason
{
    /// <summary>No ineligibility reasons identified.</summary>
    None = 0,

    /// <summary>Caller is under 18 years of age.</summary>
    CallerUnderage = 1 << 0,

    /// <summary>Candidate is under 18 years of age.</summary>
    CandidateUnderage = 1 << 1,

    /// <summary>Caller and candidate represent the same profile.</summary>
    SameProfile = 1 << 2,

    /// <summary>Caller has not explicitly opted into the Social/Dating discovery module.</summary>
    CallerNotOptedIn = 1 << 3,

    /// <summary>Caller profile is not in active standing.</summary>
    CallerNotActive = 1 << 4,

    /// <summary>Candidate has not explicitly opted into the Social/Dating discovery module.</summary>
    CandidateNotOptedIn = 1 << 5,

    /// <summary>Candidate profile is inactive, suspended, or deactivated.</summary>
    CandidateNotActive = 1 << 6,

    /// <summary>Candidate has disabled discovery or enabled hidden/ghost mode.</summary>
    CandidateHidden = 1 << 7,

    /// <summary>Candidate is on the caller's active block list.</summary>
    BlockedByCaller = 1 << 8,

    /// <summary>Caller is on the candidate's active block list.</summary>
    BlockedByCandidate = 1 << 9,

    /// <summary>Authoritative evidence or verification status is missing or unresolved (fails closed).</summary>
    MissingEvidence = 1 << 10
}

/// <summary>
/// Immutable, privacy-preserving offline representation of caller discovery eligibility evidence.
/// </summary>
public sealed class SocialDiscoveryCallerEvidence : IEquatable<SocialDiscoveryCallerEvidence>
{
    /// <summary>Gets the private Social profile identity of the discovering caller.</summary>
    [JsonIgnore]
    public Guid CallerProfileId { get; }

    /// <summary>Gets the caller's age in whole years.</summary>
    [JsonIgnore]
    public int CallerAge { get; }

    /// <summary>Gets a value indicating whether the caller has explicitly opted into Social discovery.</summary>
    [JsonIgnore]
    public bool IsOptedIn { get; }

    /// <summary>Gets a value indicating whether the caller profile is in active, good standing.</summary>
    [JsonIgnore]
    public bool IsActive { get; }

    /// <summary>Gets a value indicating whether authoritative evidence is available to evaluate the caller.</summary>
    [JsonIgnore]
    public bool HasAuthoritativeEvidence { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialDiscoveryCallerEvidence"/> class.
    /// </summary>
    public SocialDiscoveryCallerEvidence(
        Guid callerProfileId,
        int callerAge,
        bool isOptedIn,
        bool isActive,
        bool hasAuthoritativeEvidence = true)
    {
        if (callerProfileId == Guid.Empty)
        {
            throw new ArgumentException("Caller profile identity cannot be empty.", nameof(callerProfileId));
        }

        CallerProfileId = callerProfileId;
        CallerAge = callerAge;
        IsOptedIn = isOptedIn;
        IsActive = isActive;
        HasAuthoritativeEvidence = hasAuthoritativeEvidence;
    }

    public bool Equals(SocialDiscoveryCallerEvidence? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return CallerProfileId == other.CallerProfileId &&
               CallerAge == other.CallerAge &&
               IsOptedIn == other.IsOptedIn &&
               IsActive == other.IsActive &&
               HasAuthoritativeEvidence == other.HasAuthoritativeEvidence;
    }

    public override bool Equals(object? obj) => obj is SocialDiscoveryCallerEvidence other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(CallerProfileId, CallerAge, IsOptedIn, IsActive, HasAuthoritativeEvidence);

    public override string ToString() => "SocialDiscoveryCallerEvidence [private]";
}

/// <summary>
/// Immutable, privacy-preserving offline representation of candidate discovery eligibility evidence.
/// </summary>
public sealed class SocialDiscoveryCandidateEvidence : IEquatable<SocialDiscoveryCandidateEvidence>
{
    /// <summary>Gets the private Social profile identity of the target candidate.</summary>
    [JsonIgnore]
    public Guid CandidateProfileId { get; }

    /// <summary>Gets the candidate's age in whole years.</summary>
    [JsonIgnore]
    public int CandidateAge { get; }

    /// <summary>Gets a value indicating whether the candidate has explicitly opted into Social discovery.</summary>
    [JsonIgnore]
    public bool IsOptedIn { get; }

    /// <summary>Gets a value indicating whether the candidate profile is active and in good standing.</summary>
    [JsonIgnore]
    public bool IsActive { get; }

    /// <summary>Gets a value indicating whether the candidate is currently visible to discovery.</summary>
    [JsonIgnore]
    public bool IsDiscoverable { get; }

    /// <summary>Gets a value indicating whether the candidate is blocked by the caller.</summary>
    [JsonIgnore]
    public bool IsBlockedByCaller { get; }

    /// <summary>Gets a value indicating whether the caller is blocked by the candidate.</summary>
    [JsonIgnore]
    public bool IsCallerBlockedByCandidate { get; }

    /// <summary>Gets a value indicating whether authoritative evidence is available to evaluate the candidate.</summary>
    [JsonIgnore]
    public bool HasAuthoritativeEvidence { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialDiscoveryCandidateEvidence"/> class.
    /// </summary>
    public SocialDiscoveryCandidateEvidence(
        Guid candidateProfileId,
        int candidateAge,
        bool isOptedIn,
        bool isActive,
        bool isDiscoverable,
        bool isBlockedByCaller,
        bool isCallerBlockedByCandidate,
        bool hasAuthoritativeEvidence = true)
    {
        if (candidateProfileId == Guid.Empty)
        {
            throw new ArgumentException("Candidate profile identity cannot be empty.", nameof(candidateProfileId));
        }

        CandidateProfileId = candidateProfileId;
        CandidateAge = candidateAge;
        IsOptedIn = isOptedIn;
        IsActive = isActive;
        IsDiscoverable = isDiscoverable;
        IsBlockedByCaller = isBlockedByCaller;
        IsCallerBlockedByCandidate = isCallerBlockedByCandidate;
        HasAuthoritativeEvidence = hasAuthoritativeEvidence;
    }

    public bool Equals(SocialDiscoveryCandidateEvidence? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return CandidateProfileId == other.CandidateProfileId &&
               CandidateAge == other.CandidateAge &&
               IsOptedIn == other.IsOptedIn &&
               IsActive == other.IsActive &&
               IsDiscoverable == other.IsDiscoverable &&
               IsBlockedByCaller == other.IsBlockedByCaller &&
               IsCallerBlockedByCandidate == other.IsCallerBlockedByCandidate &&
               HasAuthoritativeEvidence == other.HasAuthoritativeEvidence;
    }

    public override bool Equals(object? obj) => obj is SocialDiscoveryCandidateEvidence other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CandidateProfileId);
        hash.Add(CandidateAge);
        hash.Add(IsOptedIn);
        hash.Add(IsActive);
        hash.Add(IsDiscoverable);
        hash.Add(IsBlockedByCaller);
        hash.Add(IsCallerBlockedByCandidate);
        hash.Add(HasAuthoritativeEvidence);
        return hash.ToHashCode();
    }

    public override string ToString() => "SocialDiscoveryCandidateEvidence [private]";
}

/// <summary>
/// Immutable evaluation result from the Social discovery eligibility pre-filter.
/// </summary>
public sealed class SocialDiscoveryEligibilityResult : IEquatable<SocialDiscoveryEligibilityResult>
{
    /// <summary>Gets the primary eligibility evaluation status.</summary>
    public SocialDiscoveryEligibilityStatus Status { get; }

    /// <summary>Gets the combined exclusion and indeterminate reasons.</summary>
    public SocialDiscoveryIneligibilityReason Reasons { get; }

    /// <summary>Gets a value indicating whether the candidate is confirmed eligible.</summary>
    public bool IsEligible => Status == SocialDiscoveryEligibilityStatus.Eligible;

    private SocialDiscoveryEligibilityResult(
        SocialDiscoveryEligibilityStatus status,
        SocialDiscoveryIneligibilityReason reasons)
    {
        Status = status;
        Reasons = reasons;
    }

    /// <summary>Creates a successful, eligible evaluation result.</summary>
    public static SocialDiscoveryEligibilityResult Success() =>
        new(SocialDiscoveryEligibilityStatus.Eligible, SocialDiscoveryIneligibilityReason.None);

    /// <summary>Creates an ineligible evaluation result with specified reasons.</summary>
    public static SocialDiscoveryEligibilityResult Ineligible(SocialDiscoveryIneligibilityReason reasons) =>
        new(SocialDiscoveryEligibilityStatus.Ineligible, reasons);

    /// <summary>Creates an indeterminate evaluation result (fails closed) with specified reasons.</summary>
    public static SocialDiscoveryEligibilityResult Indeterminate(SocialDiscoveryIneligibilityReason reasons) =>
        new(SocialDiscoveryEligibilityStatus.Indeterminate, reasons);

    public bool Equals(SocialDiscoveryEligibilityResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Status == other.Status && Reasons == other.Reasons;
    }

    public override bool Equals(object? obj) => obj is SocialDiscoveryEligibilityResult other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Status, Reasons);

    public override string ToString() => "SocialDiscoveryEligibilityResult [private]";
}

/// <summary>
/// Deterministic offline domain pre-filter enforcing age, module opt-in, profile standing,
/// visibility, bidirectional block clearance, and fail-closed privacy safeguards before candidate discovery.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Privacy Boundaries (SOC-02 / CF-04):</b><br />
/// This component provides pure, in-memory domain evaluation. It does NOT invoke external APIs, execute database
/// queries, or access cross-module identity data.
/// </para>
/// <para>
/// <b>Fail-Closed Security Invariant:</b><br />
/// If authoritative evidence is missing, unverified, or indeterminate for either party, the pre-filter strictly
/// fails closed, returning an indeterminate/ineligible outcome to prevent unauthorized candidate exposure.
/// </para>
/// </remarks>
public static class SocialDiscoveryEligibilityPreFilter
{
    private const int MinimumLegalAge = 18;

    /// <summary>
    /// Evaluates a candidate against caller discovery requirements and privacy boundaries.
    /// </summary>
    /// <param name="caller">The discovering caller evidence.</param>
    /// <param name="candidate">The candidate profile evidence.</param>
    /// <returns>A deterministic <see cref="SocialDiscoveryEligibilityResult"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="caller"/> or <paramref name="candidate"/> is null.</exception>
    public static SocialDiscoveryEligibilityResult Evaluate(
        SocialDiscoveryCallerEvidence caller,
        SocialDiscoveryCandidateEvidence candidate)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(candidate);

        var reasons = SocialDiscoveryIneligibilityReason.None;

        // 1. Fail-closed check: authoritative evidence must be present for both participants
        if (!caller.HasAuthoritativeEvidence || !candidate.HasAuthoritativeEvidence)
        {
            reasons |= SocialDiscoveryIneligibilityReason.MissingEvidence;
            return SocialDiscoveryEligibilityResult.Indeterminate(reasons);
        }

        // 2. Identity scope check: candidate cannot be the caller
        if (caller.CallerProfileId == candidate.CandidateProfileId)
        {
            reasons |= SocialDiscoveryIneligibilityReason.SameProfile;
        }

        // 3. Age eligibility checks (minimum 18 years)
        if (caller.CallerAge < MinimumLegalAge)
        {
            reasons |= SocialDiscoveryIneligibilityReason.CallerUnderage;
        }

        if (candidate.CandidateAge < MinimumLegalAge)
        {
            reasons |= SocialDiscoveryIneligibilityReason.CandidateUnderage;
        }

        // 4. Opt-in and standing checks
        if (!caller.IsOptedIn)
        {
            reasons |= SocialDiscoveryIneligibilityReason.CallerNotOptedIn;
        }

        if (!caller.IsActive)
        {
            reasons |= SocialDiscoveryIneligibilityReason.CallerNotActive;
        }

        if (!candidate.IsOptedIn)
        {
            reasons |= SocialDiscoveryIneligibilityReason.CandidateNotOptedIn;
        }

        if (!candidate.IsActive)
        {
            reasons |= SocialDiscoveryIneligibilityReason.CandidateNotActive;
        }

        // 5. Visibility / discovery setting check
        if (!candidate.IsDiscoverable)
        {
            reasons |= SocialDiscoveryIneligibilityReason.CandidateHidden;
        }

        // 6. Bidirectional block list checks
        if (candidate.IsBlockedByCaller)
        {
            reasons |= SocialDiscoveryIneligibilityReason.BlockedByCaller;
        }

        if (candidate.IsCallerBlockedByCandidate)
        {
            reasons |= SocialDiscoveryIneligibilityReason.BlockedByCandidate;
        }

        if (reasons != SocialDiscoveryIneligibilityReason.None)
        {
            return SocialDiscoveryEligibilityResult.Ineligible(reasons);
        }

        return SocialDiscoveryEligibilityResult.Success();
    }

    /// <summary>
    /// Filters a batch of candidates, yielding only those who satisfy all eligibility and privacy pre-filter criteria.
    /// </summary>
    /// <param name="caller">The discovering caller evidence.</param>
    /// <param name="candidates">The batch of candidate evidence records.</param>
    /// <returns>An immutable read-only list of eligible candidates.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="caller"/> or <paramref name="candidates"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="candidates"/> contains a null element.</exception>
    public static IReadOnlyList<SocialDiscoveryCandidateEvidence> FilterEligible(
        SocialDiscoveryCallerEvidence caller,
        IEnumerable<SocialDiscoveryCandidateEvidence> candidates)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(candidates);

        var list = new List<SocialDiscoveryCandidateEvidence>();

        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                throw new ArgumentException("Candidate evidence collection cannot contain null elements.", nameof(candidates));
            }

            var result = Evaluate(caller, candidate);
            if (result.IsEligible)
            {
                list.Add(candidate);
            }
        }

        return Array.AsReadOnly(list.ToArray());
    }
}
