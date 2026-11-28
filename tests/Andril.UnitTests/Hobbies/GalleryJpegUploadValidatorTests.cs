using System.Security.Cryptography;
using Andril.Hobbies.Media;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class GalleryJpegUploadValidatorTests
{
    [Fact]
    public async Task Validation_DecodesJpegAndEnforcesContentAndResourceBounds()
    {
        var jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAx" +
            "NDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIy" +
            "MjIyMjIyMjL/wAARCAADAAMDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF" +
            "BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW" +
            "V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi" +
            "4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC" +
            "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVm" +
            "Z2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq" +
            "8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5/ooooA//2Q==");
        var validator = new GalleryJpegUploadValidator(jpeg.Length, 3, 27);
        var result = Assert.IsType<GalleryValidatedUpload>(
            await validator.ValidateAsync(new MemoryStream(jpeg), "image/jpeg", default));
        Assert.Equal((3, 3), (result.Width, result.Height));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(jpeg)), result.Sha256);
        Assert.Equal(jpeg, result.Bytes.ToArray());

        Assert.Null(await validator.ValidateAsync(new MemoryStream(jpeg), "image/png", default));
        Assert.Null(await validator.ValidateAsync(new MemoryStream(), "image/jpeg", default));
        Assert.Null(await validator.ValidateAsync(new MemoryStream([255, 216, 255, 217]), "image/jpeg", default));
        Assert.Null(await validator.ValidateAsync(new MemoryStream(jpeg[..^1]), "image/jpeg", default));
        Assert.Null(await validator.ValidateAsync(new MemoryStream([.. jpeg, 0]), "image/jpeg", default));
        Assert.Null(await new GalleryJpegUploadValidator(jpeg.Length, 2, 27)
            .ValidateAsync(new MemoryStream(jpeg), "image/jpeg", default));
        Assert.Null(await new GalleryJpegUploadValidator(jpeg.Length, 3, 26)
            .ValidateAsync(new MemoryStream(jpeg), "image/jpeg", default));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            validator.ValidateAsync(new MemoryStream(jpeg), "image/jpeg", cancellation.Token));
    }
}
