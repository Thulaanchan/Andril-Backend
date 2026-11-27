using JpegLibrary;

namespace Andril.Hobbies.Map.Contributions;

internal sealed class MapJpegInspector : IMapJpegInspector
{
    // Bound decoder work and temporary coefficient storage independently of upload size.
    private const long MaximumDecodedSamples = 64L * 1024 * 1024;
    private static readonly SemaphoreSlim DecodeSlots = new(2, 2);

    public async Task<bool> IsValidJpegAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length is < 4 or > SupabaseMapPrivateMediaStorage.MaximumBytes
            || bytes.Span[0] != 0xff || bytes.Span[1] != 0xd8
            || bytes.Span[^2] != 0xff || bytes.Span[^1] != 0xd9)
            return false;

        await DecodeSlots.WaitAsync(cancellationToken);
        try
        {
            var decoder = new JpegDecoder();
            decoder.SetInput(bytes);
            if (decoder.Identify() != bytes.Length || decoder.Width <= 0 || decoder.Height <= 0
                || decoder.NumberOfComponents is < 1 or > 4
                || (long)decoder.Width * decoder.Height * decoder.NumberOfComponents > MaximumDecodedSamples)
                return false;

            var output = new ValidationOutput(cancellationToken);
            decoder.SetOutputWriter(output);
            decoder.Decode();
            cancellationToken.ThrowIfCancellationRequested();
            return output.Components == (1 << decoder.NumberOfComponents) - 1;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException
            or ArgumentException or NotSupportedException or IndexOutOfRangeException or ArithmeticException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            DecodeSlots.Release();
        }
    }

    private sealed class ValidationOutput(CancellationToken cancellationToken) : JpegBlockOutputWriter
    {
        public int Components { get; private set; }

        public override void WriteBlock(ref short blockRef, int componentIndex, int x, int y)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)componentIndex >= 4)
                throw new InvalidDataException("Invalid JPEG component index.");
            Components |= 1 << componentIndex;
        }
    }
}
