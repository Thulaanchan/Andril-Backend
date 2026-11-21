using System.Buffers.Binary;
using Andril.Common.Accounts.Profiles;

namespace Andril.ProfilePhotoProcessor;

internal static class ProfileImageContainer
{
    private static readonly uint[] PngCrcTable = CreatePngCrcTable();

    internal static byte[]? Validate(ReadOnlySpan<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1..8].SequenceEqual("PNG\r\n\u001a\n"u8))
            return ValidatePng(bytes[8..], ct);
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8)
            return ValidateJpeg(bytes[2..], ct);
        throw new PhotoUploadInvalidException();
    }

    private static byte[]? ValidatePng(ReadOnlySpan<byte> chunks, CancellationToken ct)
    {
        if (chunks.Length < 25 || BinaryPrimitives.ReadUInt32BigEndian(chunks) != 13
            || !chunks[4..8].SequenceEqual("IHDR"u8))
            throw new PhotoUploadInvalidException();
        var width = BinaryPrimitives.ReadUInt32BigEndian(chunks[8..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(chunks[12..]);
        if (width > int.MaxValue || height > int.MaxValue) throw new PhotoUploadInvalidException();
        ProfileImageNormalizer.ValidateDimensions((int)width, (int)height);
        if (chunks[16] != 8 || chunks[17] is not (0 or 2 or 4 or 6)
            || chunks[18] != 0 || chunks[19] != 0 || chunks[20] > 1)
            throw new PhotoUploadInvalidException();
        bool first = true;
        bool hasIdat = false;
        bool hasData = false;
        bool dataEnded = false;
        bool hasSrgb = false;
        bool hasGamma = false;
        bool hasChromaticities = false;
        ReadOnlySpan<byte> exif = default;
        while (chunks.Length >= 12)
        {
            ct.ThrowIfCancellationRequested();
            uint length = BinaryPrimitives.ReadUInt32BigEndian(chunks);
            if (length > chunks.Length - 12) throw new PhotoUploadInvalidException();
            var type = chunks[4..8];
            var data = chunks.Slice(8, (int)length);
            ValidatePngChunk(chunks.Slice(4, 4 + (int)length),
                BinaryPrimitives.ReadUInt32BigEndian(chunks[(8 + (int)length)..]), ct);
            if (type.SequenceEqual("IHDR"u8) != first
                || type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8)
                || type.SequenceEqual("iCCP"u8) || type.SequenceEqual("cICP"u8))
                throw new PhotoUploadInvalidException();
            first = false;
            if (type.SequenceEqual("sRGB"u8))
            {
                if (hasSrgb || hasIdat || length != 1 || data[0] > 3) throw new PhotoUploadInvalidException();
                hasSrgb = true;
            }
            if (type.SequenceEqual("gAMA"u8))
            {
                if (hasGamma || hasIdat || length != 4 || BinaryPrimitives.ReadUInt32BigEndian(data) != 45455)
                    throw new PhotoUploadInvalidException();
                hasGamma = true;
            }
            if (type.SequenceEqual("cHRM"u8))
            {
                if (hasChromaticities || hasIdat || !HasSrgbChromaticities(data)) throw new PhotoUploadInvalidException();
                hasChromaticities = true;
            }
            if (type.SequenceEqual("eXIf"u8))
                exif = ReadExif(exif, data);
            if (type.SequenceEqual("IDAT"u8))
            {
                if (dataEnded) throw new PhotoUploadInvalidException();
                hasIdat = true;
                hasData |= length != 0;
            }
            else if (hasIdat) dataEnded = true;
            if (type.SequenceEqual("IEND"u8))
            {
                if (!hasData || length != 0 || chunks.Length != 12) throw new PhotoUploadInvalidException();
                return exif.IsEmpty ? null : exif.ToArray();
            }
            chunks = chunks[(12 + (int)length)..];
        }
        throw new PhotoUploadInvalidException();
    }

    private static bool HasSrgbChromaticities(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<int> srgb = [31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000];
        if (data.Length != srgb.Length * 4) return false;
        foreach (int coordinate in srgb)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(data) != coordinate) return false;
            data = data[4..];
        }
        return true;
    }

    private static void ValidatePngChunk(ReadOnlySpan<byte> chunk, uint expectedCrc, CancellationToken ct)
    {
        foreach (byte letter in chunk[..4])
            if (letter is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
                throw new PhotoUploadInvalidException();
        if (chunk[2] >= (byte)'a') throw new PhotoUploadInvalidException();
        var type = chunk[..4];
        if (chunk[0] <= (byte)'Z' && !type.SequenceEqual("IHDR"u8) && !type.SequenceEqual("PLTE"u8)
            && !type.SequenceEqual("IDAT"u8) && !type.SequenceEqual("IEND"u8))
            throw new PhotoUploadInvalidException();

        uint crc = uint.MaxValue;
        for (int i = 0; i < chunk.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            crc = PngCrcTable[(byte)(crc ^ chunk[i])] ^ (crc >> 8);
        }
        if (~crc != expectedCrc) throw new PhotoUploadInvalidException();
    }

    private static uint[] CreatePngCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
            table[i] = value;
        }
        return table;
    }

    // Follow marker boundaries and skip entropy data. A suffix check alone would accept joined images.
    private static byte[]? ValidateJpeg(ReadOnlySpan<byte> bytes, CancellationToken ct)
    {
        bool inScan = false;
        bool hasFrame = false;
        bool hasScan = false;
        bool hasEntropy = false;
        int components = 0;
        int? adobeTransform = null;
        ReadOnlySpan<byte> exif = default;
        while (!bytes.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();
            if (inScan)
            {
                int marker = bytes.IndexOf((byte)0xff);
                if (marker < 0) break;
                hasEntropy |= marker > 0;
                bytes = bytes[marker..];
            }
            if (bytes[0] != 0xff) break;
            do { bytes = bytes[1..]; } while (!bytes.IsEmpty && bytes[0] == 0xff);
            if (bytes.IsEmpty) break;
            byte code = bytes[0];
            bytes = bytes[1..];
            if (inScan && (code == 0 || code is >= 0xd0 and <= 0xd7))
            {
                hasEntropy |= code == 0;
                continue;
            }
            if (inScan && !hasEntropy) break;
            inScan = false;
            if (code == 0xd9 && hasScan && bytes.IsEmpty)
            {
                if (adobeTransform == 1 && components != 3) break;
                return exif.IsEmpty ? null : exif.ToArray();
            }
            if (code is 0 or 1 or >= 0xd0 and <= 0xd9 || bytes.Length < 2) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes);
            if (length < 2 || length > bytes.Length) break;
            var payload = bytes[2..length];
            if (code == 0xe2 && payload.StartsWith("MPF\0"u8)) break;
            if (code == 0xe2 && payload.StartsWith("ICC_PROFILE\0"u8)) break;
            if (code == 0xee && payload.StartsWith("Adobe"u8))
            {
                if (adobeTransform.HasValue || payload.Length != 12 || payload[11] > 1) break;
                adobeTransform = payload[11];
            }
            if (code == 0xe1 && payload.StartsWith("Exif\0\0"u8))
                exif = ReadExif(exif, payload[6..]);
            if (code is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (hasFrame || code is not (0xc0 or 0xc1 or 0xc2) || payload.Length < 6) break;
                components = payload[5];
                if (payload[0] != 8 || components is not (1 or 3) || payload.Length != 6 + 3 * components) break;
                hasFrame = true;
                ProfileImageNormalizer.ValidateDimensions(BinaryPrimitives.ReadUInt16BigEndian(payload[3..]),
                    BinaryPrimitives.ReadUInt16BigEndian(payload[1..]));
            }
            if (code == 0xda)
            {
                if (!hasFrame || payload.Length < 1 || payload[0] < 1 || payload[0] > components
                    || payload.Length != 4 + 2 * payload[0]) break;
                hasEntropy = false;
                inScan = hasScan = true;
            }
            bytes = bytes[length..];
        }
        throw new PhotoUploadInvalidException();
    }

    private static ReadOnlySpan<byte> ReadExif(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> data)
    {
        // Validate the TIFF header and first directory bounds before invoking the EXIF parser.
        if (!previous.IsEmpty || data.Length < 8) throw new PhotoUploadInvalidException();
        uint offset;
        if (data.StartsWith("II*\0"u8))
            offset = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        else if (data.StartsWith("MM\0*"u8))
            offset = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        else
            throw new PhotoUploadInvalidException();
        if (offset < 8 || offset > data.Length - 2) throw new PhotoUploadInvalidException();
        var directory = data[(int)offset..];
        int count = data[0] == (byte)'I'
            ? BinaryPrimitives.ReadUInt16LittleEndian(directory)
            : BinaryPrimitives.ReadUInt16BigEndian(directory);
        if (directory.Length < 2 + 12 * count + 4) throw new PhotoUploadInvalidException();
        return data;
    }
}
