
using Andril.Common.Accounts.Profiles;
using Andril.Common.Policies;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ModuleProfilePhotoPolicyTests
{
    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    public void LearningAndHobbies_DoNotRequirePhoto(
        string module)
    {
        Assert.False(
            ModuleProfilePhotoPolicy.RequiresApprovedPhoto(module));

        Assert.True(
            ModuleProfilePhotoPolicy.MeetsPhotoRequirement(
                module, null));
    }

    [Fact]
    public void Social_RequiresApprovedPhoto()
    {
        Assert.True(
            ModuleProfilePhotoPolicy.RequiresApprovedPhoto(
                "social"));
    }

    [Fact]
    public void Social_WithoutPhoto_DoesNotMeetRequirement()
    {
        Assert.False(
            ModuleProfilePhotoPolicy.MeetsPhotoRequirement(
                "social", null));
    }

    [Theory]
    [InlineData(PhotoState.PendingValidation, PhotoReason.None)]
    [InlineData(PhotoState.PendingReview, PhotoReason.ReviewRequired)]
    [InlineData(PhotoState.Rejected, PhotoReason.NoFace)]
    [InlineData(PhotoState.Rejected, PhotoReason.Blurred)]
    [InlineData(PhotoState.Rejected, PhotoReason.MultipleFaces)]
    [InlineData(PhotoState.Approved, PhotoReason.ReviewRequired)]
    public void Social_UnapprovedOrInvalidPhoto_DoesNotMeetRequirement(
        PhotoState state,
        PhotoReason reason)
    {
        var photo = new PrivatePhotoStatus(
            Guid.NewGuid(),
            state,
            reason);

        Assert.False(
            ModuleProfilePhotoPolicy.MeetsPhotoRequirement(
                "social", photo));
    }

    [Fact]
    public void Social_ApprovedPhoto_MeetsPhotoRequirement()
    {
        var photo = new PrivatePhotoStatus(
            Guid.NewGuid(),
            PhotoState.Approved,
            PhotoReason.None);

        Assert.True(
            ModuleProfilePhotoPolicy.MeetsPhotoRequirement(
                "social", photo));
    }

    [Fact]
    public void Social_EmptyUploadId_DoesNotMeetRequirement()
    {
        var photo = new PrivatePhotoStatus(
            Guid.Empty,
            PhotoState.Approved,
            PhotoReason.None);

        Assert.False(
            ModuleProfilePhotoPolicy.MeetsPhotoRequirement(
                "social", photo));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dating")]
    [InlineData("Learning")]
    [InlineData("social ")]
    [InlineData("unknown")]
    public void InvalidModule_IsRejected(string module)
    {
        Assert.Throws<ProfileValidationException>(
            () => ModuleProfilePhotoPolicy.RequiresApprovedPhoto(
                module));

        Assert.Throws<ProfileValidationException>(
            () => ModuleProfilePhotoPolicy.MeetsPhotoRequirement(
                module, null));
    }

    [Fact]
    public void NullModule_IsRejected()
    {
        Assert.Throws<ProfileValidationException>(
            () => ModuleProfilePhotoPolicy.RequiresApprovedPhoto(
                null!));

        Assert.Throws<ProfileValidationException>(
            () => ModuleProfilePhotoPolicy.MeetsPhotoRequirement(
                null!, null));
    }
}
