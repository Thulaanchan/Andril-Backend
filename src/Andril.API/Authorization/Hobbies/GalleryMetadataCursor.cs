using System.Buffers.Binary;
using System.Security.Cryptography;
using Andril.Hobbies.DTOs;
using Andril.Hobbies.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Andril.API.Authorization.Hobbies;

// Explicit internal composition. Production key custody and token lifetime are separate decisions.
internal sealed class GalleryMetadataCursor(IDataProtectionProvider protection)
{
    private const byte Version = 1;
    private const int PayloadSize = 25;
    internal const int MaximumEncodedLength = 1024;

    internal string Encode(GalleryActor actor, GalleryPagePosition position)
    {
        if (!IsHobbiesOwner(actor))
            throw new ArgumentException("A verified Hobbies owner is required.", nameof(actor));
        GalleryPaging.Validate(actor.ModuleProfileId, 1, 1, position, default);

        var payload = new byte[PayloadSize];
        payload[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), position.Timestamp.Ticks);
        position.Id.TryWriteBytes(payload.AsSpan(9));
        var encoded = WebEncoders.Base64UrlEncode(Protector(actor).Protect(payload));
        if (encoded.Length is 0 or > MaximumEncodedLength)
            throw new InvalidOperationException("Gallery cursor protection is unavailable.");
        return encoded;
    }

    internal bool TryDecode(GalleryActor actor, string token, out GalleryPagePosition? position)
    {
        position = null;
        if (!IsHobbiesOwner(actor) || token.Length is 0 or > MaximumEncodedLength ||
            token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            return false;

        byte[] protectedPayload;
        try
        {
            protectedPayload = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }
        if (!string.Equals(WebEncoders.Base64UrlEncode(protectedPayload), token, StringComparison.Ordinal))
            return false;

        var protector = Protector(actor);
        byte[] payload;
        try
        {
            payload = protector.Unprotect(protectedPayload);
        }
        catch (CryptographicException)
        {
            // Invalid authentication and missing decryption keys share a non-disclosing outcome.
            return false;
        }

        if (payload.Length != PayloadSize || payload[0] != Version)
            return false;
        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(1, 8));
        var id = new Guid(payload.AsSpan(9));
        if (id == Guid.Empty || ticks <= DateTimeOffset.MinValue.Ticks ||
            ticks >= DateTimeOffset.MaxValue.Ticks || ticks % 10 != 0)
            return false;

        position = new GalleryPagePosition(actor.ModuleProfileId, actor.Module, new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }

    private IDataProtector Protector(GalleryActor actor) =>
        protection.CreateProtector("Andril.Gallery.Cursor", "v1", actor.Module,
            actor.ModuleProfileId.ToString("N"), "owned-metadata", "created-desc.id-asc");

    private static bool IsHobbiesOwner(GalleryActor actor) =>
        actor.IsActive && actor.ModuleProfileId != Guid.Empty && actor.Module == "Hobbies";
}
