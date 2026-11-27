using Andril.Learning.Interfaces;
using System.Text.Json.Serialization;

namespace Andril.Learning.Discovery;

/// <summary>Ordinary Learning discovery is a distinct disclosure purpose from AI tool processing.</summary>
public sealed record LearningPeopleEvidence([property: JsonIgnore] Guid ProfileId, [property: JsonIgnore] string Module, [property: JsonIgnore] string? DisplayName,
    [property: JsonIgnore] LearningDiscoveryLifecycle Lifecycle, [property: JsonIgnore] LearningDiscoveryDecision Eligibility,
    [property: JsonIgnore] LearningDiscoveryDecision Visibility, [property: JsonIgnore] LearningDiscoveryDecision Blocking,
    [property: JsonIgnore] LearningDiscoveryDecision OrdinaryDisplayNameDisclosure)
{
    public override string ToString() => "Learning People evidence [private fields omitted]";
}
public sealed record LearningPeopleSnapshot([property: JsonIgnore] Guid AccountId, [property: JsonIgnore] Guid ProfileId, [property: JsonIgnore] string AuthorizationRevision,
    [property: JsonIgnore] IReadOnlyList<LearningPeopleEvidence> Candidates)
{
    public override string ToString() => "Learning People snapshot [private fields omitted]";
}
/// <summary>
/// New ordinary-discovery technical boundary: resolve actual current Learning requester and
/// target lifecycle, visibility, both-direction blocking and purpose-specific disclosure from
/// authoritative stores; no AI field flag or Social match grants ordinary disclosure.
/// Keep exact content/classification, blocking and lifecycle coordinated through callback
/// release. Return null if unknown/revoked. Query is a bounded search, not authorization.
/// No production fact writer, ranking, quota or visibility policy is inferred here.
/// </summary>
public interface ILearningPeopleDiscoveryReader
{
    Task<T?> WithAuthorizedSearchAsync<T>(LearningSetupScope scope, string query, int limit,
        Func<LearningPeopleSnapshot, CancellationToken, Task<T>> project, CancellationToken cancellationToken = default) where T : class;
}
