using System.Globalization;
using System.Text.Json.Serialization;

namespace Andril.Learning.Entities;

/// <summary>
/// Learning-owned opaque representation of a persisted generation/version pair, not a store,
/// authority receipt, generation allocator or in-memory revision substitute. Construction and
/// parsing prove structure only. The approved database lifecycle must ensure non-reuse and
/// atomic monotonic advancement. No initial version or deletion/backfill policy is implied.
/// </summary>
public sealed class LearningSetupRevision
{
    [JsonIgnore] public Guid GenerationId { get; }
    [JsonIgnore] public long Version { get; }

    private LearningSetupRevision(Guid generationId, long version)
    { GenerationId = generationId; Version = version; }

    public static LearningSetupRevision FromPersisted(Guid generationId, long version)
    {
        if (generationId == Guid.Empty) throw new ArgumentException("A revision generation is required.", nameof(generationId));
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version), "Revision version must be nonnegative.");
        return new(generationId, version);
    }

    /// <summary>Versioned canonical wire representation only, with no account/profile identity.
    /// Consumers must treat the entire value as opaque. Legacy snapshot/hash tokens are rejected.</summary>
    public string ToOpaqueToken() => "lr1." + GenerationId.ToString("N") + "." + Version.ToString("x16", CultureInfo.InvariantCulture);

    public static bool TryParse(string? token, out LearningSetupRevision? revision)
    {
        revision = null;
        if (token is null || token.Length != 53 || !token.StartsWith("lr1.", StringComparison.Ordinal) || token[36] != '.'
            || !Guid.TryParseExact(token.AsSpan(4, 32), "N", out var generation) || generation == Guid.Empty
            || !long.TryParse(token.AsSpan(37), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var version) || version < 0)
            return false;
        var parsed = new LearningSetupRevision(generation, version);
        if (!string.Equals(parsed.ToOpaqueToken(), token, StringComparison.Ordinal)) return false;
        revision = parsed;
        return true;
    }

    public override string ToString() => "Private Learning revision [generation and version omitted; not authorization]";
}
