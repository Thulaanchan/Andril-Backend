
using System;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class NotificationModuleScopeTests
{
    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public void ValidModule_ShouldPreserveScope(
        NotificationModule module)
    {
        var profileId = Guid.NewGuid();

        var scope = new NotificationModuleScope(
            module,
            profileId);

        Assert.Equal(module, scope.Module);
        Assert.Equal(
            profileId,
            scope.RecipientModuleProfileId);
    }

    [Fact]
    public void InvalidModule_ShouldBeRejected()
    {
        var profileId = Guid.NewGuid();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NotificationModuleScope(
                (NotificationModule)999,
                profileId));
    }

    [Fact]
    public void EmptyProfileId_ShouldBeRejected()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new NotificationModuleScope(
                NotificationModule.Learning,
                Guid.Empty));

        Assert.Equal(
            "recipientModuleProfileId",
            exception.ParamName);
    }
}
