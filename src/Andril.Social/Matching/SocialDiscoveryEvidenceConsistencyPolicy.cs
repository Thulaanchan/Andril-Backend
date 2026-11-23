using System;
using System.Collections.Generic;

namespace Andril.Social.Matching;

/// <summary>
/// Status outcome of the evidence consistency validation between candidate discovery evidence and matching input.
/// </summary>
public enum SocialDiscoveryEvidenceConsistencyStatus
{
    /// <summary>Evidence and matching input identities, ages, and authoritative flags are consistent.</summary>
    Valid,

    /// <summary>Identity or demographic mismatch detected between evidence and matching input.</summary>
    Mismatch,

    /// <summary>Authoritative evidence is unconfirmed or missing (fails closed).</summary>
    MissingEvidence,

    /// <summary>One or more candidate profile identifiers are empty or malformed.</summary>
    InvalidIdentity
}

/// <summary>
/// Granular inconsistency reasons detected between candidate discovery evidence and matching input.
/// </summary>
[Flags]
public enum SocialDiscoveryEvidenceInconsistencyReason
{
    /// <summary>No inconsistency detected.</summary>
    None = 0,

    /// <summary>Candidate profile identity is an empty GUID.</summary>
    EmptyCandidateId = 1 << 0,

    /// <summary>Private profile identity differs between evidence and matching input.</summary>
    ProfileIdMismatch = 1 << 1,

    /// <summary>Demographic age differs between evidence and matching input.</summary>
    AgeMismatch = 1 << 2,

    /// <summary>Authoritative evidence flag is false or unconfirmed.</summary>
    MissingAuthoritativeEvidence = 1 << 3
}

/// <summary>
/// Immutable, privacy-safe result of candidate discovery evidence consistency evaluation.
/// </summary>
public sealed class SocialDiscoveryEvidenceConsistencyResult : IEquatable<SocialDiscoveryEvidenceConsistencyResult>
{
    /// <summary>Gets the primary consistency validation status.</summary>
    public SocialDiscoveryEvidenceConsistencyStatus Status { get; }

    /// <summary>Gets the detailed inconsistency reasons detected.</summary>
    public SocialDiscoveryEvidenceInconsistencyReason Reasons { get; }

    /// <summary>Gets a value indicating whether the pair is confirmed consistent.</summary>
    public bool IsValid => Status == SocialDiscoveryEvidenceConsistencyStatus.Valid;

    private SocialDiscoveryEvidenceConsistencyResult(
        SocialDiscoveryEvidenceConsistencyStatus status,
        SocialDiscoveryEvidenceInconsistencyReason reasons)
    {
        Status = status;
        Reasons = reasons;
    }

    /// <summary>Creates a valid consistency result.</summary>
    public static SocialDiscoveryEvidenceConsistencyResult Success() =>
        new(SocialDiscoveryEvidenceConsistencyStatus.Valid, SocialDiscoveryEvidenceInconsistencyReason.None);

    /// <summary>Creates an invalid identity consistency result.</summary>
    public static SocialDiscoveryEvidenceConsistencyResult Invalid(SocialDiscoveryEvidenceInconsistencyReason reasons) =>
        new(SocialDiscoveryEvidenceConsistencyStatus.InvalidIdentity, reasons);

    /// <summary>Creates a mismatch consistency result.</summary>
    public static SocialDiscoveryEvidenceConsistencyResult Mismatch(SocialDiscoveryEvidenceInconsistencyReason reasons) =>
        new(SocialDiscoveryEvidenceConsistencyStatus.Mismatch, reasons);

    /// <summary>Creates a missing evidence consistency result.</summary>
    public static SocialDiscoveryEvidenceConsistencyResult Missing(SocialDiscoveryEvidenceInconsistencyReason reasons) =>
        new(SocialDiscoveryEvidenceConsistencyStatus.MissingEvidence, reasons);

    public bool Equals(SocialDiscoveryEvidenceConsistencyResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Status == other.Status && Reasons == other.Reasons;
    }

    public override bool Equals(object? obj) =>
        obj is SocialDiscoveryEvidenceConsistencyResult other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Status, Reasons);

    public override string ToString() => "SocialDiscoveryEvidenceConsistencyResult [private]";
}

/// <summary>
/// Deterministic offline policy validating identity, age, and authoritative provenance consistency
/// between candidate discovery evidence and corresponding explore matching input (SOC-02 / PRI).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// This policy performs pure in-memory consistency validation. It does NOT infer account ownership,
/// verify active session provenance, perform database lookups, or authorize communication between profiles.
/// </para>
/// </remarks>
public static class SocialDiscoveryEvidenceConsistencyPolicy
{
    /// <summary>
    /// Validates consistency between discovery evidence and candidate matching input.
    /// </summary>
    /// <param name="evidence">The candidate's discovery eligibility evidence.</param>
    /// <param name="candidate">The candidate's explore matching input.</param>
    /// <returns>A deterministic <see cref="SocialDiscoveryEvidenceConsistencyResult"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="evidence"/> or <paramref name="candidate"/> is null.</exception>
    public static SocialDiscoveryEvidenceConsistencyResult Validate(
        SocialDiscoveryCandidateEvidence evidence,
        SocialCandidateMatchingInput candidate)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(candidate);

        var reasons = SocialDiscoveryEvidenceInconsistencyReason.None;

        if (evidence.CandidateProfileId == Guid.Empty || candidate.CandidateProfileId == Guid.Empty)
        {
            reasons |= SocialDiscoveryEvidenceInconsistencyReason.EmptyCandidateId;
            return SocialDiscoveryEvidenceConsistencyResult.Invalid(reasons);
        }

        if (evidence.CandidateProfileId != candidate.CandidateProfileId)
        {
            reasons |= SocialDiscoveryEvidenceInconsistencyReason.ProfileIdMismatch;
        }

        if (evidence.CandidateAge != candidate.CandidateAge)
        {
            reasons |= SocialDiscoveryInconsistencyReason(reasons, SocialDiscoveryEvidenceInconsistencyReason.AgeMismatch);
        }

        if (!evidence.HasAuthoritativeEvidence)
        {
            reasons |= SocialDiscoveryEvidenceInconsistencyReason.MissingAuthoritativeEvidence;
            return SocialDiscoveryEvidenceConsistencyResult.Missing(reasons);
        }

        if (reasons != SocialDiscoveryEvidenceInconsistencyReason.None)
        {
            return SocialDiscoveryEvidenceConsistencyResult.Mismatch(reasons);
        }

        return SocialDiscoveryEvidenceConsistencyResult.Success();
    }

    private static SocialDiscoveryEvidenceInconsistencyReason SocialDiscoveryInconsistencyReason(
        SocialDiscoveryEvidenceInconsistencyReason current,
        SocialDiscoveryEvidenceInconsistencyReason flag) => current | flag;
}
