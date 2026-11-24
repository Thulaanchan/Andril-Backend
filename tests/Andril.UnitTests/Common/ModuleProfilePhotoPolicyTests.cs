using Andril.Common.Accounts.Profiles;
using Andril.Common.DTOs;
using Andril.Common.Policies;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ModuleProfilePhotoPolicyTests
{
    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    public void OptionalModulesNeedNoPhoto(string module)
    {
        Assert.False(ModuleProfilePhotoPolicy.RequiresStoredPhoto(module));
        Assert.True(ModuleProfilePhotoPolicy.MeetsPhotoRequirement(new(Guid.NewGuid(), module), null));
    }

    [Fact]
    public void SocialRequiresCurrentStoredEvidenceForTheExactProfile()
    {
        var scope = new PhotoScope(Guid.NewGuid(), "social");
        var photo = new StoredProfilePhoto(new(scope, Guid.NewGuid()), PhotoState.Stored, true,
            new string('a', 64), "image/jpeg", 1, 300, 300);
        Assert.True(ModuleProfilePhotoPolicy.RequiresStoredPhoto(scope.Module));
        Assert.True(ModuleProfilePhotoPolicy.MeetsPhotoRequirement(scope, photo));
        Assert.False(ModuleProfilePhotoPolicy.MeetsPhotoRequirement(scope, null));
        Assert.False(ModuleProfilePhotoPolicy.MeetsPhotoRequirement(scope, photo with { State = PhotoState.PendingStorage }));
        Assert.False(ModuleProfilePhotoPolicy.MeetsPhotoRequirement(scope, photo with { State = PhotoState.Failed }));
        Assert.False(ModuleProfilePhotoPolicy.MeetsPhotoRequirement(scope, photo with { IsCurrent = false }));
        Assert.False(ModuleProfilePhotoPolicy.MeetsPhotoRequirement(scope, photo with { Address = new(scope, Guid.Empty) }));
        Assert.False(ModuleProfilePhotoPolicy.MeetsPhotoRequirement(scope, photo with { Address = new(new(Guid.NewGuid(), "social"), Guid.NewGuid()) }));
        Assert.False(ModuleProfilePhotoPolicy.MeetsPhotoRequirement(scope, photo with { Address = new(scope with { Module = "hobbies" }, Guid.NewGuid()) }));
    }

    [Theory]
    [InlineData("dating")]
    [InlineData("Social")]
    [InlineData("Learning")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("social ")]
    [InlineData(" learning")]
    public void RejectsInvalidModule(string? module)
    {
        Assert.Throws<ProfileValidationException>(() => ModuleProfilePhotoPolicy.RequiresStoredPhoto(module!));
        Assert.Throws<ProfileValidationException>(() => ModuleProfilePhotoPolicy.MeetsPhotoRequirement(new(Guid.NewGuid(), module!), null));
    }
}
