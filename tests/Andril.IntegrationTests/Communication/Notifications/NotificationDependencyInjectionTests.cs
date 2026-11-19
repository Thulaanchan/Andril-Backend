
using System;
using System.Linq;
using System.Threading.Tasks;
using Andril.API.Extensions;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Andril.Communication.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

public sealed class NotificationDependencyInjectionTests
{
    [Fact]
    public void AddNotifications_ShouldRegisterFailClosedDependencies()
    {
        var services = new ServiceCollection();

        services.AddNotifications();

        var repository = Assert.Single(
    services,
    x => x.ServiceType == typeof(INotificationRepository));

        Assert.Equal(
            typeof(DenyAllNotificationRepository),
            repository.ImplementationType);

        Assert.Equal(
            ServiceLifetime.Scoped,
            repository.Lifetime);

        var access = Assert.Single(
    services,
    x => x.ServiceType == typeof(INotificationRepository));

        Assert.Equal(
            typeof(DenyAllNotificationRecipientAccessReader),
            access.ImplementationType);

        Assert.Equal(
            ServiceLifetime.Scoped,
            access.Lifetime);

        var application = Assert.Single(
    services,
    x => x.ServiceType == typeof(INotificationRepository));

        Assert.Equal(
            ServiceLifetime.Scoped,
            application.Lifetime);
    }

    [Fact]
    public void AddNotifications_ShouldResolveApplicationService()
    {
        var services = new ServiceCollection();

        services.AddNotifications();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

        using var scope = provider.CreateScope();

        var repository = scope.ServiceProvider
            .GetRequiredService<INotificationRepository>();

        var access = scope.ServiceProvider
            .GetRequiredService<INotificationRecipientAccessReader>();

        var application = scope.ServiceProvider
            .GetRequiredService<NotificationApplicationService>();

        Assert.IsType<DenyAllNotificationRepository>(
            repository);

        Assert.IsType<DenyAllNotificationRecipientAccessReader>(
            access);

        Assert.NotNull(application);
    }

    [Fact]
    public void AddNotifications_ShouldNotCreateDuplicateRegistrations()
    {
        var services = new ServiceCollection();

        services.AddNotifications();
        services.AddNotifications();

        Assert.Single(
    services,
    x => x.ServiceType == typeof(INotificationRepository));

        Assert.Single(
    services,
    x => x.ServiceType == typeof(INotificationRepository));

        Assert.Single(
    services,
    x => x.ServiceType == typeof(INotificationRepository));
    }

    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task DenyAllRegistration_ShouldNeverExposeNotifications(
        NotificationModule module)
    {
        var services = new ServiceCollection();

        services.AddNotifications();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

        using var scope = provider.CreateScope();

        var application = scope.ServiceProvider
            .GetRequiredService<NotificationApplicationService>();

        var result = await application.ListLatestAsync(
            Guid.NewGuid(),
            module,
            20);

        Assert.NotNull(result);
        Assert.Empty(result);
    }
}
