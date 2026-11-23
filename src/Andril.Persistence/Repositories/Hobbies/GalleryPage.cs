using Andril.Hobbies.Entities;

namespace Andril.Persistence.Repositories.Hobbies;

// Internal database position only: not a client cursor or proof of authorization.
// The type fixes the ordering to CreatedAtUtc DESC, Id ASC.
internal sealed record GalleryPagePosition(
    Guid OwnerModuleProfileId, string Module, DateTimeOffset Timestamp, Guid Id);

internal sealed record GalleryPage(
    IReadOnlyList<GalleryMedia> Items, GalleryPagePosition? Next);
