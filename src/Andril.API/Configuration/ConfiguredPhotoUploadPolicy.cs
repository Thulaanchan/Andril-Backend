using Andril.Common.Accounts.Profiles;
using Microsoft.Extensions.Options;

namespace Andril.API.Configuration;

public sealed class ConfiguredPhotoUploadPolicy(IOptions<ProfilePhotoOptions> options, IProfileImageInspector inspector) : IPhotoUploadPolicy
{
    public int RequireMaximumBytes() => RequireReady().MaximumUploadBytes;

    public PhotoCleanupSettings RequireCleanupSettings() => RequireReady().CleanupSettings();

    private ProfilePhotoOptions RequireReady()
    {
        if (!inspector.IsApprovedAndConfigured) throw new PhotoDependencyException();
        var settings = options.Value;
        if (!settings.Enabled || !settings.HasApprovedLimits() || settings.Storage.TrustedOrigin() is null)
            throw new PhotoDependencyException();
        return settings;
    }
}
