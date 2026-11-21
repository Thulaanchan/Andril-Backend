using System.Buffers.Binary;
using Andril.Common.Accounts.Profiles;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Andril.ProfilePhotoProcessor.Tests;

public sealed class ProfileImageNormalizerTests
{
    private readonly ProfileImageNormalizer normalizer = new();

    [Theory]
    [InlineData(true, 1600, 800, 1024, 512)]
    [InlineData(false, 400, 300, 400, 300)]
    [InlineData(false, 4096, 4096, 1024, 1024)]
    public async Task ProducesBoundedJpegWithoutUpscaling(bool jpeg, int width, int height, int expectedWidth, int expectedHeight)
    {
        using var input = new Image<Rgba32>(width, height, new Rgba32(60, 120, 180, 255));
        var result = await normalizer.InspectAsync(Encode(input, jpeg ? new JpegEncoder() : new PngEncoder()), default);
        Assert.False(normalizer.IsApprovedAndConfigured);
        Assert.Equal("image/jpeg", result.MediaType);
        Assert.Equal((expectedWidth, expectedHeight), (result.Width, result.Height));
        Assert.InRange(result.Bytes.Length, 1, ProfilePhotoLimits.UploadBytes);
        Assert.Same(JpegFormat.Instance, Image.DetectFormat(result.Bytes));
        using var actual = Image.Load(result.Bytes);
        Assert.Equal((expectedWidth, expectedHeight), (actual.Width, actual.Height));
        Assert.Equal(85, actual.Metadata.GetJpegMetadata().Quality);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppliesOrientationToPixelsAndDropsOriginalMetadata(bool jpeg)
    {
        using var input = new Image<Rgba32>(400, 300, new Rgba32(250, 0, 0, 255));
        for (int y = 150; y < 300; y++)
            for (int x = 0; x < 400; x++)
                input[x, y] = new Rgba32(0, 0, 250, 255);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.Make, "Synthetic camera");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        input.Metadata.ExifProfile = exif;
        var result = await normalizer.InspectAsync(Encode(input, jpeg ? new JpegEncoder() : new PngEncoder()), default);
        using var actual = Image.Load<Rgba32>(result.Bytes);
        Assert.Equal((300, 400), (actual.Width, actual.Height));
        Assert.True(actual[50, 50].B > 200);
        Assert.True(actual[250, 50].R > 200);
        Assert.Null(actual.Metadata.ExifProfile);
        Assert.Null(actual.Metadata.IccProfile);
        Assert.Null(actual.Metadata.IptcProfile);
        Assert.Null(actual.Metadata.XmpProfile);
    }

    [Fact]
    public async Task RejectsUnsupportedMalformedTruncatedAndJoinedImages()
    {
        using var input = new Image<Rgba32>(300, 300, new Rgba32(20, 40, 60, 255));
        byte[] jpeg = Encode(input, new JpegEncoder());
        byte[] png = Encode(input, new PngEncoder());
        byte[] damagedPng = (byte[])png.Clone();
        damagedPng[29] ^= 0xff;
        byte[] damagedJpeg = (byte[])jpeg.Clone();
        damagedJpeg[2] = 0;
        byte[] brokenExifSegment = [0xff, 0xe1, 0, 9, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, 0];
        foreach (var bytes in new byte[][] { [], Encode(input, new GifEncoder()), damagedPng, damagedJpeg,
            png[..^1], jpeg[..^2], [.. jpeg, .. jpeg], [.. png, .. png], [.. jpeg[..2], .. brokenExifSegment, .. jpeg[2..]] })
        {
            var failure = await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => normalizer.InspectAsync(bytes, default));
            Assert.Null(failure.InnerException);
        }
    }

    [Fact]
    public async Task RejectsIncompleteJpegScanEvenWithAnEndMarker()
    {
        using var input = new Image<Rgb24>(400, 400, new Rgb24(20, 40, 60));
        var jpeg = Encode(input, new JpegEncoder());
        int scan = jpeg.AsSpan().IndexOf(new byte[] { 0xff, 0xda });
        Assert.True(scan > 0);
        int scanStart = scan + 2 + (jpeg[scan + 2] << 8) + jpeg[scan + 3];
        int split = scanStart + (jpeg.Length - scanStart - 2) / 2;
        byte[] incomplete = [.. jpeg[..split], 0xff, 0xd9];
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => normalizer.InspectAsync(incomplete, default));
    }

    [Fact]
    public async Task RejectsAnimatedPngAndInvalidOrientation()
    {
        using var input = new Image<Rgba32>(300, 300, new Rgba32(20, 40, 60, 255));
        input.Frames.AddFrame(input.Frames.RootFrame);
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() =>
            normalizer.InspectAsync(Encode(input, new PngEncoder()), default));
        input.Frames.RemoveFrame(1);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)9);
        input.Metadata.ExifProfile = exif;
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() =>
            normalizer.InspectAsync(Encode(input, new JpegEncoder()), default));
    }

    [Theory]
    [InlineData(299, 400)]
    [InlineData(4097, 300)]
    [InlineData(4097, 4096)]
    public async Task RejectsDimensionsOutsideExistingLimits(int width, int height)
    {
        using var input = new Image<Rgba32>(width, height, new Rgba32(20, 40, 60, 255));
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() =>
            normalizer.InspectAsync(Encode(input, new JpegEncoder()), default));
    }

    [Fact]
    public async Task BoundsBytesAndSupportsCancellationAndSubsequentJobs()
    {
        await Assert.ThrowsAsync<PhotoUploadTooLargeException>(() =>
            normalizer.InspectAsync(new byte[ProfilePhotoLimits.UploadBytes + 1], default));
        using var input = new Image<Rgba32>(300, 300, new Rgba32(20, 40, 60, 255));
        byte[] bytes = Encode(input, new PngEncoder());
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => normalizer.InspectAsync(bytes, ct.Token));
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => normalizer.InspectAsync(bytes, default)));
        Assert.All(results, result => Assert.Equal(results[0].Bytes, result.Bytes));
    }

    [Fact]
    public async Task FlattensTransparentAndHalfTransparentPngBeforeDownsampling()
    {
        using var transparent = new Image<Rgba32>(2048, 600, new Rgba32(0, 0, 0, 0));
        transparent.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
                rows.GetRowSpan(y)[1024..].Fill(new Rgba32(200, 60, 20, 128));
        });
        var normalized = await normalizer.InspectAsync(Encode(transparent, new PngEncoder()), default);
        Assert.Equal((1024, 300), (normalized.Width, normalized.Height));
        using var result = Image.Load<Rgb24>(normalized.Bytes);
        AssertNear(result[50, 150], new Rgb24(255, 255, 255));
        AssertNear(result[900, 150], new Rgb24(227, 157, 137));
        // The edge may vary with the resampler and JPEG, but it must not darken below the composited side.
        for (int x = 508; x <= 516; x++)
            Assert.True(result[x, 150].B >= 125);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptsProgressiveJpegAndEightBitGrayscale(bool grayscale)
    {
        using var source = new Image<L8>(320, 300, new L8(110));
        foreach (IImageEncoder encoder in new IImageEncoder[]
        {
            new JpegEncoder { Progressive = true, ColorType = grayscale ? JpegColorType.Luminance : JpegColorType.YCbCrRatio420 },
            new PngEncoder { ColorType = grayscale ? PngColorType.Grayscale : PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 }
        })
        {
            var output = await normalizer.InspectAsync(Encode(source, encoder), default);
            using var decoded = Image.Load<Rgb24>(output.Bytes);
            AssertNear(decoded[100, 100], new Rgb24(110, 110, 110));
        }
    }

    [Fact]
    public async Task RejectsUnapprovedColorEncodings()
    {
        using var highDepth = new Image<Rgba64>(300, 300);
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => normalizer.InspectAsync(
            Encode(highDepth, new PngEncoder { BitDepth = PngBitDepth.Bit16 }), default));
        using var colorManaged = new Image<Rgb24>(300, 300);
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() =>
            normalizer.InspectAsync(Encode(colorManaged, new JpegEncoder { ColorType = JpegColorType.Cmyk }), default));
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() =>
            normalizer.InspectAsync(Encode(colorManaged, new PngEncoder { Gamma = 1 }), default));
        colorManaged.Metadata.IccProfile = new IccProfile();
        foreach (IImageEncoder encoder in new IImageEncoder[] { new JpegEncoder(), new PngEncoder() })
            await Assert.ThrowsAsync<PhotoUploadInvalidException>(() =>
                normalizer.InspectAsync(Encode(colorManaged, encoder), default));
    }

    [Fact]
    public async Task RejectsWebpAndPngWithoutImageData()
    {
        using var source = new Image<Rgb24>(300, 300);
        byte[] webp = Encode(source, new WebpEncoder());
        Assert.Same(WebpFormat.Instance, Image.DetectFormat(webp));
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => normalizer.InspectAsync(webp, default));
        byte[] png = Encode(source, new PngEncoder());
        using var missingData = new MemoryStream();
        missingData.Write(png.AsSpan(0, 8));
        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (!png.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8))
                missingData.Write(png.AsSpan(offset, length + 12));
            offset += length + 12;
        }
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() =>
            normalizer.InspectAsync(missingData.ToArray(), default));
    }

    [Fact]
    public async Task TwoSlotsBlockTheThirdAndReleaseAfterCancellationAndUnexpectedFailure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<SafeProfileImage> HoldSlot(CancellationToken ct)
        {
            if (Interlocked.Increment(ref entered) == 2) bothEntered.SetResult();
            await release.Task.WaitAsync(ct);
            throw new InvalidOperationException("synthetic decoder diagnostics");
        }
        var first = ProfileImageNormalizer.WithDecodeSlotAsync(() => HoldSlot(cancel.Token), cancel.Token);
        var second = ProfileImageNormalizer.WithDecodeSlotAsync(() => HoldSlot(timeout.Token), timeout.Token);
        try
        {
            await bothEntered.Task.WaitAsync(timeout.Token);
            using var source = new Image<Rgb24>(300, 300);
            var third = normalizer.InspectAsync(Encode(source, new PngEncoder()), timeout.Token);
            Assert.False(third.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal("image/jpeg", (await third).MediaType);
        }
        finally
        {
            release.TrySetResult();
            cancel.Cancel();
        }
        var error = await Assert.ThrowsAsync<PhotoDependencyException>(() => second);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic decoder diagnostics", error.Message);
        using var image = new Image<Rgb24>(300, 300);
        byte[] png = Encode(image, new PngEncoder());
        int reacquired = 0;
        var bothReacquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<SafeProfileImage> CheckPermit()
        {
            if (Interlocked.Increment(ref reacquired) == 2) bothReacquired.SetResult();
            await bothReacquired.Task.WaitAsync(timeout.Token);
            return new(png, "image/png", 300, 300);
        }
        await Task.WhenAll(ProfileImageNormalizer.WithDecodeSlotAsync(CheckPermit, timeout.Token),
            ProfileImageNormalizer.WithDecodeSlotAsync(CheckPermit, timeout.Token));
    }

    private static void AssertNear(Rgb24 actual, Rgb24 expected)
    {
        Assert.InRange(Math.Abs(actual.R - expected.R), 0, 5);
        Assert.InRange(Math.Abs(actual.G - expected.G), 0, 5);
        Assert.InRange(Math.Abs(actual.B - expected.B), 0, 5);
    }

    private static byte[] Encode(Image image, IImageEncoder encoder)
    {
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }
}
