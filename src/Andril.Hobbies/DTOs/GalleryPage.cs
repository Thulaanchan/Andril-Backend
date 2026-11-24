using Andril.Hobbies.Entities;

namespace Andril.Hobbies.DTOs;

// Internal database position only: not a client cursor or proof of authorization.
// The type fixes the ordering to CreatedAtUtc DESC, Id ASC.
public sealed record GalleryPagePosition(
    Guid OwnerModuleProfileId, string Module, DateTimeOffset Timestamp, Guid Id);

public sealed record GalleryPage(
    IReadOnlyList<GalleryMedia> Items, GalleryPagePosition? Next);

// Backend-only metadata result. This is not an approved HTTP response or cursor format.
public sealed record GalleryPhotoPage(
    IReadOnlyList<GalleryPhotoDto> Items, GalleryPagePosition? Next);

internal static class GalleryPaging
{
    internal static void Validate(Guid authorizedOwnerModuleProfileId, int pageSize,
        int trustedTestOnlyMaximum, GalleryPagePosition? after, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authorizedOwnerModuleProfileId == Guid.Empty)
            throw new ArgumentException("A trusted owner scope is required.", nameof(authorizedOwnerModuleProfileId));
        if (trustedTestOnlyMaximum <= 0 || trustedTestOnlyMaximum == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(trustedTestOnlyMaximum));
        if (pageSize <= 0 || pageSize > trustedTestOnlyMaximum)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (after is not null && (after.OwnerModuleProfileId != authorizedOwnerModuleProfileId
            || after.Module != "Hobbies" || after.Id == Guid.Empty
            || after.Timestamp.Offset != TimeSpan.Zero || after.Timestamp == DateTimeOffset.MinValue
            || after.Timestamp == DateTimeOffset.MaxValue || after.Timestamp.Ticks % 10 != 0))
            throw new ArgumentException("The continuation position is invalid for this scope.", nameof(after));
    }
}
