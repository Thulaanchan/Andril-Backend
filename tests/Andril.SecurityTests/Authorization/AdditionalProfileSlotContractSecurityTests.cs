using Andril.API.Controllers.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andril.SecurityTests.Authorization;

public sealed class AdditionalProfileSlotContractSecurityTests
{
    [Fact]
    public async Task DraftSlotTransportCannotReadWriteDeleteOrDisclosePrivateObjectKeys()
    {
        Assert.True(Attribute.IsDefined(typeof(AdditionalProfilePhotosController), typeof(NonControllerAttribute)));
        var controller = new AdditionalProfilePhotosController(new User(Guid.NewGuid(), true));
        await Assert.ThrowsAsync<PhotoDependencyException>(() => controller.GetSlots("learning", default));
        await Assert.ThrowsAsync<PhotoDependencyException>(() => controller.GetSlot("learning", 1, default));
        await Assert.ThrowsAsync<PhotoDependencyException>(() => controller.GetSlotContent("learning", 1, default));
        await Assert.ThrowsAsync<PhotoDependencyException>(() => controller.UploadToSlot("learning", 1, default));
        await Assert.ThrowsAsync<PhotoDependencyException>(() => controller.DeleteSlot("learning", 1, default));
    }

    [Fact]
    public async Task AuthenticationModuleAndSlotValidationRemainRestrictive()
    {
        var anonymous = new AdditionalProfilePhotosController(new User(null, false));
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => anonymous.GetSlots("learning", default));
        var controller = new AdditionalProfilePhotosController(new User(Guid.NewGuid(), true));
        await Assert.ThrowsAsync<ProfileValidationException>(() => controller.GetSlots("fourth", default));
        await Assert.ThrowsAsync<InvalidPhotoSlotException>(() => controller.UploadToSlot("social", 4, default));
    }

    private sealed record User(Guid? UserId, bool IsAuthenticated) : ICurrentUser;
}
