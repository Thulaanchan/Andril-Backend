namespace Andril.Learning.Progress;

public sealed record LearningXpThreshold(string Key, long MinimumTotal);
/// <summary>
/// Versioned configuration structure, not an approved earning policy. Empty/unapproved is
/// the default. Technical implementation supports lifetime total/per-event occurrence caps;
/// no time-window, reset, paid entitlement or conversion semantics are inferred.
/// </summary>
public sealed record LearningXpPolicy(string Revision, bool Approved, IReadOnlyDictionary<string,long> EventAmounts,
    long? MaximumTotal, IReadOnlyDictionary<string,int> MaximumOccurrences,
    IReadOnlyList<LearningXpThreshold> Levels, IReadOnlyList<LearningXpThreshold> Achievements)
{
    public static LearningXpPolicy Unconfigured { get; } = new("",false,new Dictionary<string,long>(),null,new Dictionary<string,int>(),[],[]);
    public bool IsConfigured => Approved && !string.IsNullOrWhiteSpace(Revision) && EventAmounts.Count > 0 &&
        EventAmounts.All(x => !string.IsNullOrWhiteSpace(x.Key) && x.Key.Length <= 128 && x.Value > 0) &&
        (MaximumTotal is null || MaximumTotal > 0) && MaximumOccurrences.All(x => EventAmounts.ContainsKey(x.Key) && x.Value > 0) &&
        ValidThresholds(Levels) && ValidThresholds(Achievements);
    public string CanonicalConfiguration() => System.Text.Json.JsonSerializer.Serialize(new
    {
        Revision,Approved,EventAmounts=EventAmounts.OrderBy(x=>x.Key,StringComparer.Ordinal),MaximumTotal,
        MaximumOccurrences=MaximumOccurrences.OrderBy(x=>x.Key,StringComparer.Ordinal),
        Levels=Levels.OrderBy(x=>x.Key,StringComparer.Ordinal),Achievements=Achievements.OrderBy(x=>x.Key,StringComparer.Ordinal)
    });
    private static bool ValidThresholds(IReadOnlyList<LearningXpThreshold> items) => items is not null &&
        items.All(x => x is not null && !string.IsNullOrWhiteSpace(x.Key) && x.MinimumTotal >= 0) && items.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() == items.Count;
    public override string ToString() => "Learning XP policy [configuration omitted; approval must be verified]";
}
public sealed record LearningXpProgress(long Total, string? Level, IReadOnlyList<string> Achievements, string? PolicyRevision);
/// <summary>Read only current independently verified Product/M1/M3/M6-approved configuration; null/unconfigured denies awards.</summary>
public interface ILearningXpPolicyAuthority
{
    Task<LearningXpPolicy?> ReadCurrentPolicyAsync(CancellationToken cancellationToken = default);
}
/// <summary>Independently authenticate the publisher and verify exact Product/M1/M3/M6 approvals, not a client Approved flag.</summary>
public interface ILearningXpPolicyPublicationAuthority
{
    Task<LearningXpPolicy?> ResolveApprovedPublicationAsync(Andril.Learning.Interfaces.LearningSetupScope scope,string revision,CancellationToken cancellationToken=default);
}
public enum LearningXpAdjustmentKind { Reversal = 1, Correction = 2 }
public sealed record LearningXpAdjustment(Guid OperationId, Guid OriginalCreditId, LearningXpAdjustmentKind Kind)
{
    public override string ToString()=>"Private Learning XP adjustment [references omitted]";
}
public sealed record LearningXpAdjustmentEvidence(LearningXpAwardEvidence Award, string AuditReason)
{
    public override string ToString()=>"Learning XP adjustment evidence [private audit omitted]";
}
/// <summary>Trusted audited maintenance only; no client reward/reversal/correction permissions are implied.</summary>
public interface ILearningXpAdjustmentAuthority
{
    Task<LearningXpAdjustmentEvidence?> ResolveAdjustmentAsync(Andril.Learning.Interfaces.LearningSetupScope scope,
        LearningXpAdjustment adjustment, CancellationToken cancellationToken = default);
}
