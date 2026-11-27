using System.Text.Json.Serialization;
using Andril.Learning.Interfaces;

namespace Andril.Learning.Progress;

public enum LearningXpOutcome { Unavailable, Available, Credited, AlreadyCredited, Denied, Conflict, CommitUnknown, PolicyStored }
/// <summary>Server event identity, never a user-supplied amount, verification or eligibility flag.</summary>
public sealed record LearningXpSource(string Kind, Guid EventId)
{
    public override string ToString() => "Learning XP source [private reference omitted]";
}
/// <summary>Backend assertion only: constructing this record does not prove approved earning rules.</summary>
public sealed record LearningXpAwardEvidence(
    [property: JsonIgnore] Guid AccountId, [property: JsonIgnore] Guid LearningProfileId,
    [property: JsonIgnore] LearningXpSource Source, [property: JsonIgnore] long Amount,
    [property: JsonIgnore] string PolicyRevision, [property: JsonIgnore] string EvidenceRevision)
{
    public override string ToString() => "Learning XP evidence [private facts omitted]";
}
/// <summary>
/// New provisional earning integration. Independently authenticate a trusted event producer,
/// verify actual current approved earning policy/amount and durable source completion, exact
/// Learning owner and current eligibility. Never accept an amount/verified flag from a client,
/// infer verification from self-reported Growth, or convert Flight Points or purchases to XP.
/// Unknown/missing policy, source, cap/reversal rule or authority returns null/false.
/// Implementations must coordinate source/lifecycle revocation with the enclosing transaction
/// and revalidate the exact evidence at commit/read release. No permissive default, policy,
/// event selection, award value, verification promotion or production implementation supplied.
/// </summary>
public interface ILearningXpAuthority
{
    Task<bool> MayReadAsync(LearningSetupScope scope, CancellationToken cancellationToken = default);
    Task<LearningXpAwardEvidence?> ResolveAwardAsync(LearningSetupScope scope, LearningXpSource source, CancellationToken cancellationToken = default);
    Task<bool> RevalidateAwardAsync(LearningSetupScope scope, LearningXpAwardEvidence evidence, CancellationToken cancellationToken = default);
}
public sealed record LearningXpEntry(Guid Id, long Amount, DateTimeOffset CreditedAtUtc)
{
    public override string ToString() => "Private Learning XP entry [values omitted]";
}
public sealed record LearningXpResult(LearningXpOutcome Outcome, long? Total = null,
    IReadOnlyList<LearningXpEntry>? Entries = null, LearningXpProgress? Progress = null)
{
    public override string ToString() => $"Learning XP outcome: {Outcome}";
}
public interface ILearningXpRepository
{
    Task<LearningXpResult> ReadAsync(Guid? after = null, int limit = 50, CancellationToken cancellationToken = default);
    /// <summary>Trusted server-event processing only. No user-facing award HTTP route.</summary>
    Task<LearningXpResult> CreditAsync(LearningXpSource source, CancellationToken cancellationToken = default);
    Task<LearningXpResult> AdjustAsync(LearningXpAdjustment adjustment, CancellationToken cancellationToken = default);
    /// <summary>Trusted publication only; request contains a revision, not client earning values.</summary>
    Task<LearningXpResult> StageApprovedPolicyAsync(string revision,CancellationToken cancellationToken=default);
}
