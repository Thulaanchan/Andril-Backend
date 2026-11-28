namespace Andril.Social.Interfaces;

/// <summary>Owner-scoped projections of affirmative interest and committed mutual matches.</summary>
public enum SocialRelationshipList
{
    SentLikes = 1,
    MutualConnections = 2
}

public readonly record struct SocialRelationshipListAnchor(DateTime RecordedAtUtc, Guid PeerProfileId);

public sealed record SocialRelationshipListEntry(Guid PeerProfileId, string DisplayName, DateTime RecordedAtUtc, Guid? MatchId);

/// <summary>
/// Reads a bounded newest-first page in the caller's authorized Social profile transaction.
/// A repository may return one extra entry to establish a continuation cursor.
/// </summary>
public interface ISocialRelationshipListRepository
{
    const int DefaultPageSize = 20;
    const int MaximumPageSize = 50;

    Task<IReadOnlyList<SocialRelationshipListEntry>> ListAsync(Guid ownerProfileId,
        SocialRelationshipList list, SocialRelationshipListAnchor? before, int pageSize,
        CancellationToken cancellationToken = default);
}
