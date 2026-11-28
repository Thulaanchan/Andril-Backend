using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
using Xunit;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.SecurityTests.Authorization;

public sealed class ProfileSetupSecurityTests
{
    [Fact]
    public void AdditionalPhoto_EnforcesSlotBoundary_1To3()
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        // Slots below 1 or above 3 must fail closed
        Assert.Throws<InvalidPhotoSlotException>(() =>
            AdditionalProfilePhotoRecord.Create(profileId, accountId, ModuleNames.Social, 0, "key", "test.jpg", "image/jpeg", 100, DateTimeOffset.UtcNow));

        Assert.Throws<InvalidPhotoSlotException>(() =>
            AdditionalProfilePhotoRecord.Create(profileId, accountId, ModuleNames.Social, 4, "key", "test.jpg", "image/jpeg", 100, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DisplayName_ReservedKeywords_Rejected()
    {
        // Reserved system words like "andril", "admin", "moderator" must be rejected by validator
        Assert.False(Andril.Common.Validators.PrivateProfileFieldValidator.TryNormalizeDisplayName("admin", out _));
        Assert.False(Andril.Common.Validators.PrivateProfileFieldValidator.TryNormalizeDisplayName("andril", out _));
        Assert.False(Andril.Common.Validators.PrivateProfileFieldValidator.TryNormalizeDisplayName("moderator", out _));
        Assert.False(Andril.Common.Validators.PrivateProfileFieldValidator.TryNormalizeDisplayName("support", out _));
    }

    [Fact]
    public void DisplayName_InvalidUnicodeControl_Rejected()
    {
        // Null char, isolated surrogates, and formatting controls
        Assert.False(Andril.Common.Validators.PrivateProfileFieldValidator.TryNormalizeDisplayName("Alex\0Hacker", out _));
        Assert.False(Andril.Common.Validators.PrivateProfileFieldValidator.TryNormalizeDisplayName("Al\uD800ex", out _));
    }
}
