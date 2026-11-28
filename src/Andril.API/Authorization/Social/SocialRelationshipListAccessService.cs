using System.Buffers.Binary;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Interfaces;
using Microsoft.AspNetCore.WebUtilities;

namespace Andril.API.Authorization.Social;

public sealed class SocialRelationshipListAccessService(IPrivateProfileService profiles, ISocialRelationshipListRepository repository)
{
    private const int CursorSize = 25;

    public Task<SocialRelationshipPage> ListAsync(SocialRelationshipList list, int pageSize, string? cursor,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > ISocialRelationshipListRepository.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        var before = cursor is null ? (SocialRelationshipListAnchor?)null : DecodeCursor(list, cursor);
        return profiles.WithAuthorizedProfileAsync("social", async profile =>
        {
            var entries = await repository.ListAsync(profile.ProfileId, list, before, pageSize, cancellationToken);
            var items = entries.Take(pageSize).ToArray();
            return new SocialRelationshipPage(items,
                entries.Count > pageSize ? EncodeCursor(list, items[^1]) : null);
        }, cancellationToken);
    }

    private static SocialRelationshipListAnchor DecodeCursor(SocialRelationshipList list, string encoded)
    {
        if (encoded.Length != 34) throw new ArgumentException("Invalid page cursor.", nameof(encoded));
        byte[] bytes;
        try { bytes = WebEncoders.Base64UrlDecode(encoded); }
        catch (FormatException) { throw new ArgumentException("Invalid page cursor.", nameof(encoded)); }

        if (bytes.Length != CursorSize || bytes[0] != (byte)list || WebEncoders.Base64UrlEncode(bytes) != encoded)
            throw new ArgumentException("Invalid page cursor.", nameof(encoded));
        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8));
        var peerId = new Guid(bytes.AsSpan(9));
        if (ticks <= 0 || ticks >= DateTime.MaxValue.Ticks || peerId == Guid.Empty)
            throw new ArgumentException("Invalid page cursor.", nameof(encoded));
        return new SocialRelationshipListAnchor(new DateTime(ticks, DateTimeKind.Utc), peerId);
    }

    private static string EncodeCursor(SocialRelationshipList list, SocialRelationshipListEntry entry)
    {
        var bytes = new byte[CursorSize];
        bytes[0] = (byte)list;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1, 8), entry.RecordedAtUtc.Ticks);
        entry.PeerProfileId.TryWriteBytes(bytes.AsSpan(9));
        return WebEncoders.Base64UrlEncode(bytes);
    }
}

public sealed record SocialRelationshipPage(IReadOnlyList<SocialRelationshipListEntry> Items, string? NextCursor);
