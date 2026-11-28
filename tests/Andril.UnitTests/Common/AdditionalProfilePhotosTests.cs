using Andril.Common.Accounts.Profiles;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class AdditionalProfilePhotosTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PS_T32_ValidSlotIndices_Succeed(int slotIndex)
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var photo = AdditionalProfilePhotoRecord.Create(
            profileId, accountId, "learning", slotIndex, $"learning/{profileId}/additional/{slotIndex}.jpg",
            "pic.jpg", "image/jpeg", 1024, now);

        Assert.Equal(slotIndex, photo.SlotIndex);
        Assert.Equal(profileId, photo.ProfileId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(10)]
    public void PS_T32_InvalidSlotIndices_ThrowsInvalidPhotoSlotException(int invalidSlot)
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        Assert.Throws<InvalidPhotoSlotException>(() =>
            AdditionalProfilePhotoRecord.Create(
                profileId, accountId, "learning", invalidSlot, "key", "pic.jpg", "image/jpeg", 1024, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PS_T33_ReplacePhoto_UpdatesFields()
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var photo = AdditionalProfilePhotoRecord.Create(
            profileId, accountId, "learning", 1, "key1", "pic1.jpg", "image/jpeg", 500, now);

        var replaceTime = now.AddMinutes(10);
        photo.Replace("key2", "pic2.jpg", "image/png", 1500, replaceTime);

        Assert.Equal("key2", photo.ObjectKey);
        Assert.Equal("pic2.jpg", photo.OriginalFileName);
        Assert.Equal("image/png", photo.MediaType);
        Assert.Equal(1500, photo.FileSizeBytes);
        Assert.Equal(replaceTime, photo.UpdatedAtUtc);
    }
}
