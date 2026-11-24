namespace Andril.Hobbies.Map.Places;

/// <summary>Internal offline orchestration; no DI, API, persistence or permission activation.</summary>
internal sealed class MapAttractionLifecycleService
{
    private readonly IMapAttractionLifecycleWriter _writer;

    internal MapAttractionLifecycleService(IMapAttractionLifecycleWriter writer)
        => _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    internal async Task<MapAttractionLifecycleResult> ChangeAsync(MapAttractionLifecycleChange change,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(change);
        try
        {
            var result = await _writer.ChangeAsync(change, cancellationToken);
            // No cancellation check after a writer result: the operation may have committed.
            if (result is null) return new(MapAttractionLifecycleOutcome.OutcomeUnknown);
            switch (result.Outcome)
            {
                case MapAttractionLifecycleOutcome.Changed:
                case MapAttractionLifecycleOutcome.Unchanged:
                case MapAttractionLifecycleOutcome.ExistingAuthorized:
                    if (result.RequestId != change.RequestId || result.AttractionId != change.AttractionId
                        || result.IsActive != change.IsActive || result.Version is not long version
                        || !MatchesVersion(result.Outcome, change.ExpectedVersion, version))
                        return new(MapAttractionLifecycleOutcome.OutcomeUnknown);
                    return result;
                case MapAttractionLifecycleOutcome.Conflict:
                case MapAttractionLifecycleOutcome.Denied:
                case MapAttractionLifecycleOutcome.Unavailable:
                case MapAttractionLifecycleOutcome.OutcomeUnknown:
                    return new(result.Outcome);
                default:
                    return new(MapAttractionLifecycleOutcome.OutcomeUnknown);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Unexpected failures cannot establish non-commit. Do not expose or retry them.
            return new(MapAttractionLifecycleOutcome.OutcomeUnknown);
        }
    }

    private static bool MatchesVersion(MapAttractionLifecycleOutcome outcome, long expected, long actual)
    {
        var unchanged = actual == expected;
        var changed = expected < long.MaxValue && actual == expected + 1;
        return outcome switch
        {
            MapAttractionLifecycleOutcome.Changed => changed,
            MapAttractionLifecycleOutcome.Unchanged => unchanged,
            MapAttractionLifecycleOutcome.ExistingAuthorized => unchanged || changed,
            _ => false
        };
    }
}
