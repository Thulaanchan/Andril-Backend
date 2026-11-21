using Andril.Common.Accounts.Profiles;

namespace Andril.API.Configuration;

public sealed class ProfilePhotoOptions
{
    public const string ApprovedBucket = "andril-profile-photos";

    public bool Enabled { get; init; }
    public int MaximumUploadBytes { get; init; } = ProfilePhotoLimits.UploadBytes;
    public int MinimumWidth { get; init; } = ProfilePhotoLimits.InputMinimum;
    public int MinimumHeight { get; init; } = ProfilePhotoLimits.InputMinimum;
    public int MaximumWidth { get; init; } = ProfilePhotoLimits.InputMaximum;
    public int MaximumHeight { get; init; } = ProfilePhotoLimits.InputMaximum;
    public int MaximumDecodedPixels { get; init; } = ProfilePhotoLimits.InputPixels;
    public int MaximumOutputWidth { get; init; } = ProfilePhotoLimits.OutputDimension;
    public int MaximumOutputHeight { get; init; } = ProfilePhotoLimits.OutputDimension;
    public int JpegQuality { get; init; } = ProfilePhotoLimits.JpegQuality;
    public int MaximumConcurrentDecodes { get; init; } = ProfilePhotoLimits.ConcurrentDecodes;
    public int CleanupBudgetSeconds { get; init; } = PhotoCleanupSettings.ApprovedBudgetSeconds;
    public int CleanupRetryDelaySeconds { get; init; } = PhotoCleanupSettings.ApprovedRetryDelaySeconds;
    public int MaximumCleanupCandidates { get; init; } = PhotoCleanupSettings.ApprovedMaximumCandidates;
    public ProfilePhotoStorageOptions Storage { get; init; } = new();

    public bool HasApprovedLimits() => MaximumUploadBytes == ProfilePhotoLimits.UploadBytes
        && MinimumWidth == ProfilePhotoLimits.InputMinimum && MinimumHeight == ProfilePhotoLimits.InputMinimum
        && MaximumWidth == ProfilePhotoLimits.InputMaximum && MaximumHeight == ProfilePhotoLimits.InputMaximum
        && MaximumDecodedPixels == ProfilePhotoLimits.InputPixels
        && MaximumOutputWidth == ProfilePhotoLimits.OutputDimension && MaximumOutputHeight == ProfilePhotoLimits.OutputDimension
        && JpegQuality == ProfilePhotoLimits.JpegQuality && MaximumConcurrentDecodes == ProfilePhotoLimits.ConcurrentDecodes
        && CleanupSettings() == PhotoCleanupSettings.Approved;

    internal PhotoCleanupSettings CleanupSettings() => new(CleanupBudgetSeconds, CleanupRetryDelaySeconds, MaximumCleanupCandidates);
}

public sealed class ProfilePhotoStorageOptions
{
    public string BucketName { get; init; } = ProfilePhotoOptions.ApprovedBucket;
    public string? ProjectUrl { get; init; }
    public string? SecretKey { get; init; }

    internal Uri? TrustedOrigin()
    {
        if (BucketName != ProfilePhotoOptions.ApprovedBucket
            || SecretKey is not { Length: > 10 and <= 512 } key || !key.StartsWith("sb_secret_", StringComparison.Ordinal)
            || key.Skip(10).Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-')
            || !Uri.TryCreate(ProjectUrl, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps
            || !origin.IsDefaultPort || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/"
            || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return null;

        const string suffix = ".supabase.co";
        var host = origin.IdnHost;
        if (!host.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var project = host[..^suffix.Length];
        return project.Length is > 0 and <= 63 && project.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')
            ? origin : null;
    }
}
