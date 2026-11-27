using Andril.Learning.Interfaces;
using System.Text.Json.Serialization;

namespace Andril.Learning.Discovery;

public sealed record LearningBrowseSourceReference([property: JsonIgnore] Guid TargetId, [property: JsonIgnore] string Revision)
{
    public override string ToString() => "Learning browse source reference [private]";
}
/// <summary>
/// New physical integration gate, not a claim that production fact ingestion exists.
/// Verify the requester, purpose and exact persisted source revisions against current
/// authoritative ordinary-disclosure/block/lifecycle sources. Enforce writer/revocation
/// coordination inside the enclosing M2 transaction through release. Unknown denies.
/// The source writer must atomically bind current eligibility/visibility/blocking and
/// permitted ordinary name to each revision. AI classification is not substitute evidence.
/// </summary>
public interface ILearningBrowseSourceAuthority
{
    Task<bool> RevalidateAsync(LearningSetupScope scope, string kind,
        IReadOnlyList<LearningBrowseSourceReference> sources, CancellationToken cancellationToken = default);
}
