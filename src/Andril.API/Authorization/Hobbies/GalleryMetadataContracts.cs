using System.Globalization;
using Andril.Hobbies.DTOs;

namespace Andril.API.Authorization.Hobbies;

internal enum GalleryMetadataStatus
{
    Success,
    Denied,
    InvalidPageSize,
    InvalidCursor,
    InvalidMediaId,
    MediaUnavailable,
    DependencyUnavailable
}

internal sealed record GalleryMetadataResult<T>(GalleryMetadataStatus Status, T? Value = null) where T : class;

internal sealed record GalleryMetadataPage(IReadOnlyList<GalleryPhotoDto> Items, string? NextCursor);

// Explicit bounds for controlled composition. No production defaults are registered.
internal sealed class GalleryPageBounds
{
    internal GalleryPageBounds(int minimum, int defaultSize, int maximum)
    {
        if (minimum < 1 || minimum > defaultSize || defaultSize > maximum || maximum == int.MaxValue)
            throw new ArgumentException("Invalid Gallery page bounds.");
        Minimum = minimum;
        DefaultSize = defaultSize;
        Maximum = maximum;
    }

    internal int Minimum { get; }
    internal int DefaultSize { get; }
    internal int Maximum { get; }

    internal bool TryResolve(string? raw, out int size)
    {
        size = DefaultSize;
        return raw is null ||
            (raw.Length is > 0 and <= 10 && raw.All(char.IsAsciiDigit) &&
                int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out size)
                && size >= Minimum && size <= Maximum);
    }
}
