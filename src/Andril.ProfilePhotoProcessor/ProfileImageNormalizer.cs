using Andril.Common.Accounts.Profiles;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Andril.ProfilePhotoProcessor;

public sealed class ProfileImageNormalizer : IProfileImageInspector
{
    private static readonly SemaphoreSlim Decoders = new(ProfilePhotoLimits.ConcurrentDecodes, ProfilePhotoLimits.ConcurrentDecodes);
    private static readonly DecoderOptions Options = new()
    {
        Configuration = CreateConfiguration(),
        MaxFrames = 1,
        SkipMetadata = true,
        SegmentIntegrityHandling = SegmentIntegrityHandling.Strict,
        ColorProfileHandling = ColorProfileHandling.Preserve
    };

    // This standalone increment has no production isolation or activation approval.
    public bool IsApprovedAndConfigured => false;

    public async Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0) throw new PhotoUploadInvalidException();
        if (bytes.Length > ProfilePhotoLimits.UploadBytes) throw new PhotoUploadTooLargeException();
        return await WithDecodeSlotAsync(() => DecodeAsync(bytes, cancellationToken), cancellationToken);
    }

    internal static async Task<SafeProfileImage> WithDecodeSlotAsync(
        Func<Task<SafeProfileImage>> decode, CancellationToken cancellationToken)
    {
        await Decoders.WaitAsync(cancellationToken);
        try
        {
            return await decode();
        }
        catch (Exception e) when (e is ImageFormatException or InvalidMemoryOperationException or InvalidDataException)
        {
            throw new PhotoUploadInvalidException();
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException
            or PhotoUploadInvalidException or PhotoDependencyException or PhotoUploadTooLargeException))
        {
            throw new PhotoDependencyException();
        }
        finally
        {
            Decoders.Release();
        }
    }

    private static async Task<SafeProfileImage> DecodeAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var exif = ProfileImageContainer.Validate(bytes, cancellationToken);
        using var input = new MemoryStream(bytes, writable: false);
        var info = await Image.IdentifyAsync(Options, input, cancellationToken);
        ValidateDimensions(info.Width, info.Height);
        input.Position = 0;
        using var image = await Image.LoadAsync<Rgba32>(Options, input, cancellationToken);
        ValidateDimensions(image.Width, image.Height);
        if (image.Frames.Count != 1) throw new PhotoUploadInvalidException();
        ApplyOrientationMetadata(image, exif);
        // Flatten before resampling so hidden RGB under transparent pixels cannot bleed into the output.
        PreparePixels(image, ReferenceEquals(info.Metadata.DecodedImageFormat, JpegFormat.Instance), cancellationToken);

        image.Mutate(operation => operation.AutoOrient());
        cancellationToken.ThrowIfCancellationRequested();
        if (Math.Max(image.Width, image.Height) > ProfilePhotoLimits.OutputDimension)
        {
            image.Mutate(operation => operation.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(ProfilePhotoLimits.OutputDimension)
            }));
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Encode from a fresh pixel surface so source format-specific metadata cannot influence the output.
        using var outputImage = new Image<Rgb24>(Options.Configuration, image.Width, image.Height);
        image.ProcessPixelRows(outputImage, (source, target) =>
        {
            for (int y = 0; y < source.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRow = source.GetRowSpan(y);
                var targetRow = target.GetRowSpan(y);
                for (int x = 0; x < sourceRow.Length; x++)
                {
                    var pixel = sourceRow[x];
                    targetRow[x] = new Rgb24(pixel.R, pixel.G, pixel.B);
                }
            }
        });
        // A non-expandable backing buffer bounds writes even if encoder output grows unexpectedly.
        using var output = new MemoryStream(new byte[ProfilePhotoLimits.UploadBytes]);
        output.SetLength(0);
        await outputImage.SaveAsync(output, new JpegEncoder
        {
            Quality = ProfilePhotoLimits.JpegQuality,
            SkipMetadata = true
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(output.ToArray(), "image/jpeg", outputImage.Width, outputImage.Height);
    }

    internal static void ValidateDimensions(int width, int height)
    {
        if (width < ProfilePhotoLimits.InputMinimum || height < ProfilePhotoLimits.InputMinimum
            || (long)width * height > ProfilePhotoLimits.InputPixels
            || width > ProfilePhotoLimits.InputMaximum || height > ProfilePhotoLimits.InputMaximum)
            throw new PhotoUploadInvalidException();
    }

    private static void ApplyOrientationMetadata(Image<Rgba32> image, byte[]? exifBytes)
    {
        if (exifBytes is null) return;
        var source = new ExifProfile(exifBytes);
        bool hasOrientation = source.TryGetValue(ExifTag.Orientation, out var orientation);
        if (source.InvalidTags.Count != 0 || (hasOrientation && orientation!.Value is < 1 or > 8))
            throw new PhotoUploadInvalidException();
        if (source.TryGetValue(ExifTag.ColorSpace, out var colorSpace) && colorSpace.Value != 1)
            throw new PhotoUploadInvalidException();
        if (!hasOrientation) return;
        var orientationOnly = new ExifProfile();
        orientationOnly.SetValue(ExifTag.Orientation, orientation!.Value);
        image.Metadata.ExifProfile = orientationOnly;
    }

    private static void PreparePixels(Image<Rgba32> image, bool jpeg, CancellationToken ct)
    {
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (ref var pixel in rows.GetRowSpan(y))
                {
                    if (pixel.A == byte.MaxValue) continue;
                    // JPEG has no alpha channel. An unwritten pixel is an invalid decode.
                    if (jpeg) throw new PhotoUploadInvalidException();
                    byte alpha = pixel.A;
                    pixel = new Rgba32(OverWhite(pixel.R, alpha), OverWhite(pixel.G, alpha),
                        OverWhite(pixel.B, alpha), byte.MaxValue);
                }
            }
        });
    }

    // Source-over blending in the accepted sRGB sample space, rounded to the nearest 8-bit sample.
    private static byte OverWhite(byte sample, byte alpha) =>
        (byte)((sample * alpha + 255 * (255 - alpha) + 127) / 255);

    private static Configuration CreateConfiguration()
    {
        int rgbaSurfaceMiB = ProfilePhotoLimits.InputPixels * 4 / (1024 * 1024);
        return new Configuration(new JpegConfigurationModule(), new PngConfigurationModule())
        {
            MaxDegreeOfParallelism = 1,
            MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
            {
                MaximumPoolSizeMegabytes = 0,
                AllocationLimitMegabytes = rgbaSurfaceMiB,
                // Budget two full-size surfaces per slot, including orientation work.
                AccumulativeAllocationLimitMegabytes = 2 * rgbaSurfaceMiB * ProfilePhotoLimits.ConcurrentDecodes
            })
        };
    }
}
