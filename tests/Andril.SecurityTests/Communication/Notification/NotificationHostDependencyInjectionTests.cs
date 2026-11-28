using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Andril.Communication.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Andril.SecurityTests.Communication.Notifications;

public sealed class NotificationHostDependencyInjectionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void ActualApiHost_UsesScopedDenyAllNotificationRegistrations(
        string environment)
    {
        using var factory = new NotificationHostFactory(environment);
        // Accessing Services starts the real Program host, including its
        // production AddNotifications path. No HTTP request is necessary.
        var services = factory.Services;
        Assert.Equal(environment,
            services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.Equal(environment,
            services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        output.WriteLine("Actual API host environment: {0}", environment);

        Assert.Equal(3, factory.NotificationDescriptors.Count);
        using var scopeA = services.CreateScope();
        using var scopeB = services.CreateScope();

        AssertScopedRegistration<INotificationRecipientAccessReader,
            DenyAllNotificationRecipientAccessReader>(factory, scopeA, scopeB);
        AssertScopedRegistration<INotificationRepository,
            DenyAllNotificationRepository>(factory, scopeA, scopeB);
        AssertScopedRegistration<NotificationApplicationService,
            NotificationApplicationService>(factory, scopeA, scopeB);
    }

    private void AssertScopedRegistration<TService, TImplementation>(
        NotificationHostFactory factory,
        IServiceScope scopeA,
        IServiceScope scopeB)
        where TService : class
        where TImplementation : class, TService
    {
        var descriptor = Assert.Single(factory.NotificationDescriptors,
            candidate => candidate.ServiceType == typeof(TService));
        Assert.False(descriptor.IsKeyedService);
        Assert.Equal(typeof(TImplementation), descriptor.ImplementationType);
        Assert.Null(descriptor.ImplementationFactory);
        Assert.Null(descriptor.ImplementationInstance);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        // Enumerate all registrations as well as checking the effective one:
        // a later grant-capable registration must not be hidden by DI ordering.
        var first = Assert.IsType<TImplementation>(
            Assert.Single(scopeA.ServiceProvider.GetServices<TService>()));
        Assert.Same(first, scopeA.ServiceProvider.GetRequiredService<TService>());
        Assert.Same(first, scopeA.ServiceProvider.GetRequiredService<TService>());
        var second = Assert.IsType<TImplementation>(
            Assert.Single(scopeB.ServiceProvider.GetServices<TService>()));
        Assert.NotSame(first, second);

        output.WriteLine("{0} -> {1}; descriptor lifetime: {2}; " +
            "single registration; same within Scope A; different in Scope B.",
            typeof(TService).FullName, typeof(TImplementation).FullName,
            descriptor.Lifetime);
    }

    private sealed class NotificationHostFactory(string environment)
        : WebApplicationFactory<Program>
    {
        public IReadOnlyList<ServiceDescriptor> NotificationDescriptors
            { get; private set; } = Array.Empty<ServiceDescriptor>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["SupabaseAuth:ProjectUrl"] =
                            "https://andril-test.supabase.co",
                        ["SupabaseAuth:SigningAlgorithm"] = "ES256",
                        ["SupabaseAuth:Audience"] = "authenticated",
                        ["SupabaseIdentity:PublishableKey"] =
                            "sb_publishable_synthetic_test_fixture",
                        ["SupabaseIdentity:AccountRequestsPerMinute"] = "60",
                        ["ConnectionStrings:AndrilAccounts"] = "",
                        ["ProfilePhotos:Enabled"] = "false"
                    }));
            builder.ConfigureLogging(logging =>
            {
                // Reuse the existing host-test workaround for Windows
                // EventLog permissions; other logging remains available.
                if (OperatingSystem.IsWindows())
                    logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(
                        static (_, _) => false);
            });
            builder.ConfigureTestServices(services =>
            {
                // Observation only: do not add, remove, replace, or call
                // AddNotifications here. These are Program's descriptors.
                NotificationDescriptors = services.Where(descriptor =>
                    descriptor.ServiceType == typeof(INotificationRecipientAccessReader) ||
                    descriptor.ServiceType == typeof(INotificationRepository) ||
                    descriptor.ServiceType == typeof(NotificationApplicationService))
                    .ToArray();
            });
        }
    }
}
