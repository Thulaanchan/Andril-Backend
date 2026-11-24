using System.Text.Json.Serialization;

namespace Andril.Learning.Interfaces;

public enum LearningSetupOperation { ReadSetup = 1, UpdateSetup = 2, EnterHome = 3 }
public enum LearningSetupAccessState { Allowed = 1, Denied = 2, Unavailable = 3 }

/// <summary>PROVISIONAL Learning-owned preflight boundary, not a shared M2 contract, HTTP input,
/// production authority or reusable authorization token. No client actor/profile parameters.</summary>
public interface ILearningSetupAccessBoundary
{
    Task<LearningSetupAccessResult> CheckAsync(LearningSetupOperation operation, CancellationToken cancellationToken = default);
}

/// <summary>
/// PROVISIONAL adapter seam. No production implementation or registration is supplied. Resolve
/// the verified subject through authoritative M2 ownership, session, standing, lifecycle, consent
/// and operation checks. Return null when authority is unavailable. Never manufacture positive
/// evidence from request IDs, stored completion flags or unverified client/provider assertions.
/// Observed/current versions represent one coherent current authorization snapshot. Missing or
/// stale evidence must fail closed. No positive TTL cache, silent retry or cancellation fallback.
/// Synthetic implementations belong only in isolated test assemblies.
/// </summary>
public interface ILearningSetupAuthorityReader
{
    Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid verifiedSubject, LearningSetupOperation operation,
        CancellationToken cancellationToken = default);
}

/// <summary>Backend-only asserted authority facts; construction is not provenance verification.
/// No field is a public transport contract. Production sourcing/coordination requires M2/M6 review.</summary>
public sealed record LearningSetupAccessEvidence(
    [property: JsonIgnore] Guid SubjectId,
    [property: JsonIgnore] Guid AccountId,
    [property: JsonIgnore] Guid LearningProfileId,
    [property: JsonIgnore] Guid ProfileOwnerAccountId,
    [property: JsonIgnore] string Module,
    [property: JsonIgnore] LearningSetupOperation Operation,
    [property: JsonIgnore] bool SessionCurrent,
    [property: JsonIgnore] bool MembershipActive,
    [property: JsonIgnore] bool StandingAllows,
    [property: JsonIgnore] bool ConsentSatisfied,
    [property: JsonIgnore] bool OperationAllowed,
    [property: JsonIgnore] bool Revoked,
    [property: JsonIgnore] bool SetupComplete,
    [property: JsonIgnore] Guid ObservedVersion,
    [property: JsonIgnore] Guid CurrentVersion)
{
    public override string ToString() => "Private Learning access evidence [facts omitted; adapter assertion only]";
}

/// <summary>Private point-in-time preflight result. Scope is not proof of ownership or an access
/// lease. B2-01 repositories must independently revalidate at release/commit coordination boundaries.</summary>
public sealed class LearningSetupAccessResult
{
    [JsonIgnore] public LearningSetupAccessState State { get; }
    [JsonIgnore] public LearningSetupScope? Scope { get; }
    private LearningSetupAccessResult(LearningSetupAccessState state, LearningSetupScope? scope)
    { State = state; Scope = scope; }
    internal static LearningSetupAccessResult Allowed(LearningSetupScope scope) => new(LearningSetupAccessState.Allowed, scope);
    internal static LearningSetupAccessResult Denied() => new(LearningSetupAccessState.Denied, null);
    internal static LearningSetupAccessResult Unavailable() => new(LearningSetupAccessState.Unavailable, null);
    public override string ToString() => "Learning access preflight [private scope omitted; no reusable grant]";
}
