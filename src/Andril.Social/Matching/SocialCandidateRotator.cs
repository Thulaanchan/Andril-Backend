using System;
using System.Collections.Generic;

namespace Andril.Social.Matching;

/// <summary>
/// Pure, deterministic in-memory domain utility for Social/Dating candidate sequence rotation (Option A).
/// </summary>
/// <remarks>
/// <para>
/// <b>Security and Business Boundaries:</b><br />
/// This utility implements strictly the deterministic ordering primitive approved in SOC-02 Decision E.
/// It operates entirely in memory on an already supplied sequence and assumes the caller has already performed
/// necessary eligibility, reciprocal filtering, block/relationship checks, and privacy verifications.
/// </para>
/// <para>
/// <b>Important:</b>
/// <list type="bullet">
/// <item><description>This utility does NOT authenticate callers or candidates.</description></item>
/// <item><description>It does NOT enforce Social 18+ eligibility or module opt-in.</description></item>
/// <item><description>It does NOT check active membership, profile completion, or account standing.</description></item>
/// <item><description>It does NOT check candidate visibility or discoverability flags.</description></item>
/// <item><description>It does NOT check bidirectional blocking, unmatching, or prior connections.</description></item>
/// <item><description>It does NOT evaluate candidate compatibility, distance, or demographic preferences.</description></item>
/// <item><description>It does NOT grant authority or access to inspect private Social profiles.</description></item>
/// </list>
/// The resulting sequence must NEVER be serialized directly into public API responses without separately
/// approved authorization, policy enforcement, and privacy-safe projection (redacting internal account IDs,
/// exact birthdates, and precise locations).
/// </para>
/// <para>
/// <b>Collection-Level Immutability:</b><br />
/// The returned <see cref="IReadOnlyList{T}"/> is a defensive snapshot wrapped in a read-only collection.
/// Subsequent mutations to the caller's source collection do not affect the returned list, and attempts to
/// mutate the returned collection via <see cref="IList{T}"/> throw <see cref="NotSupportedException"/>.
/// This provides collection-level immutability only; contained reference objects of type <typeparamref name="T"/>
/// retain their original references and are not deeply cloned or made deeply immutable.
/// </para>
/// </remarks>
public static class SocialCandidateRotator
{
    /// <summary>
    /// Performs a deterministic cyclic left-rotation on the supplied sequence of candidates using an explicit non-negative offset.
    /// </summary>
    /// <typeparam name="T">The candidate element type.</typeparam>
    /// <param name="candidates">The sequence of candidates to rotate. Must not be <see langword="null"/>.</param>
    /// <param name="offset">The non-negative rotation offset. Large offsets (including <see cref="int.MaxValue"/>) are mapped via modulo arithmetic without overflow.</param>
    /// <returns>An immutable read-only snapshot of the cyclically rotated candidates.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="candidates"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="offset"/> is negative.</exception>
    public static IReadOnlyList<T> Rotate<T>(
        IReadOnlyList<T> candidates,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                "Rotation offset cannot be negative.");
        }

        var count = candidates.Count;
        if (count == 0)
        {
            return Array.AsReadOnly(Array.Empty<T>());
        }

        if (count == 1)
        {
            return Array.AsReadOnly(new[] { candidates[0] });
        }

        var effectiveOffset = offset % count;
        var result = new T[count];
        var headLength = count - effectiveOffset;

        for (var i = 0; i < headLength; i++)
        {
            result[i] = candidates[i + effectiveOffset];
        }

        for (var i = 0; i < effectiveOffset; i++)
        {
            result[headLength + i] = candidates[i];
        }

        return Array.AsReadOnly(result);
    }
}
