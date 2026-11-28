using System.Security.Cryptography;
using JpegLibrary;

namespace Andril.Hobbies.Media;

internal sealed record GalleryValidatedUpload(ReadOnlyMemory<byte> Bytes, int Width, int Height, string Sha256);

// JPEG is the controlled format for this validator. Product formats and limits are unregistered.
internal sealed class GalleryJpegUploadValidator
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private readonly int _maximumBytes;
    private readonly int _maximumDimension;
    private readonly long _maximumSamples;

    internal GalleryJpegUploadValidator(int maximumBytes, int maximumDimension, long maximumSamples)
    {
        if (maximumBytes < 4 || maximumBytes >= Array.MaxLength || maximumDimension < 1 || maximumSamples < 1)
            throw new ArgumentException("Invalid Gallery validation limits.");
        _maximumBytes = maximumBytes;
        _maximumDimension = maximumDimension;
        _maximumSamples = maximumSamples;
    }

    internal async Task<GalleryValidatedUpload?> ValidateAsync(Stream input, string? contentType, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            return null;

        await Slots.WaitAsync(token);
        try
        {
            var buffer = new byte[_maximumBytes + 1];
            var length = await input.ReadAtLeastAsync(buffer, buffer.Length, false, token);
            token.ThrowIfCancellationRequested();
            if (length < 4 || length > _maximumBytes || buffer[0] != 0xff || buffer[1] != 0xd8 ||
                buffer[length - 2] != 0xff || buffer[length - 1] != 0xd9)
                return null;

            return ValidateJpeg(buffer.AsMemory(0, length), token);
        }
        finally
        {
            Slots.Release();
        }
    }

    private GalleryValidatedUpload? ValidateJpeg(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        try
        {
            var decoder = new JpegDecoder();
            decoder.SetInput(bytes);
            if (decoder.Identify() != bytes.Length || decoder.Width <= 0 || decoder.Height <= 0 ||
                decoder.Width > _maximumDimension || decoder.Height > _maximumDimension ||
                decoder.NumberOfComponents is < 1 or > 4 ||
                (long)decoder.Width * decoder.Height > _maximumSamples / decoder.NumberOfComponents)
                return null;

            var output = new ValidationOutput(token);
            decoder.SetOutputWriter(output);
            decoder.Decode();
            token.ThrowIfCancellationRequested();
            return output.Components == (1 << decoder.NumberOfComponents) - 1
                ? new(bytes, decoder.Width, decoder.Height, Convert.ToHexString(SHA256.HashData(bytes.Span)))
                : null;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException
            or ArgumentException or NotSupportedException or IndexOutOfRangeException or ArithmeticException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    private sealed class ValidationOutput(CancellationToken token) : JpegBlockOutputWriter
    {
        public int Components { get; private set; }

        public override void WriteBlock(ref short blockRef, int componentIndex, int x, int y)
        {
            token.ThrowIfCancellationRequested();
            if ((uint)componentIndex >= 4)
                throw new InvalidDataException("Invalid JPEG component index.");
            Components |= 1 << componentIndex;
        }
    }
}
