using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using Andril.API.Authorization.Hobbies;
using Andril.Hobbies.DTOs;
using Andril.Hobbies.Interfaces;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Andril.SecurityTests.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Andril.SecurityTests.Authorization.Hobbies;

public sealed class GalleryStartupRouteInventoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Startup_PreservesKnownRoutesWithoutActivatingGalleryOrPrivateMedia()
    {
        using var authentication = new AuthenticationFactory();
        using var factory = authentication.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                [new("ConnectionStrings:AndrilAccounts", "")]));
            // Keep the test host's startup key ring out of the Windows user profile.
            builder.ConfigureTestServices(services => services.PostConfigure<KeyManagementOptions>(
                options => options.XmlRepository = new TestKeyRepository()));
        });
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        var healthRoute = AssertRoute(routes, "health", "GET");
        Assert.NotNull(healthRoute.Metadata.GetMetadata<IAllowAnonymous>());
        AssertControllerRoute(routes, "api/v1/account/me", "GET", "Account");
        AssertControllerRoute(routes, "api/v1/account/profiles/{module}/photos/{uploadId:guid}", "GET", "ProfilePhotos");
        AssertControllerRoute(routes, "api/v1/onboarding/status", "GET", "Onboarding");
        AssertControllerRoute(routes, "api/v1/notifications/{module}/{notificationId:guid}/read", "PATCH", "Notifications");

        // Replace this activation gate with authorization tests when Gallery routes are approved.
        Assert.DoesNotContain(routes, IdentifiesGalleryAccess);
        var services = factory.Services.GetRequiredService<IServiceProviderIsService>();
        Assert.False(services.IsService(typeof(IGalleryService)));
        Assert.False(services.IsService(typeof(IGalleryPrivateMediaAccessReader)));
        Assert.False(services.IsService(typeof(HobbiesGalleryDbContext)));
        Assert.False(services.IsService(typeof(DbContextOptions<HobbiesGalleryDbContext>)));
        output.WriteLine($"Inspected {routes.Length} registered route endpoints, including the synthetic authentication probe.");
    }

    private static RouteEndpoint AssertRoute(RouteEndpoint[] routes, string pattern, string method) =>
        Assert.Single(routes, endpoint =>
            string.Equals(endpoint.RoutePattern.RawText?.TrimStart('/'), pattern, StringComparison.Ordinal)
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.Ordinal) == true);

    private static void AssertControllerRoute(RouteEndpoint[] routes, string pattern, string method, string controller)
    {
        var route = AssertRoute(routes, pattern, method);
        var action = route.Metadata.GetMetadata<ControllerActionDescriptor>();
        Assert.NotNull(action);
        Assert.Equal(controller, action.ControllerName);
    }

    private static bool IdentifiesGalleryAccess(RouteEndpoint endpoint)
    {
        var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        var handler = action?.MethodInfo ?? endpoint.Metadata.GetMetadata<MethodInfo>();
        return IdentifiesGallery(endpoint.RoutePattern.RawText)
            || IdentifiesGallery(endpoint.DisplayName)
            || IdentifiesGallery(endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            || IdentifiesGallery(action?.ControllerName)
            || IdentifiesGallery(action?.ActionName)
            || IdentifiesGallery(handler?.Name)
            || (handler is not null && (UsesGalleryContract(handler.ReturnType)
                || handler.GetParameters().Any(parameter => UsesGalleryContract(parameter.ParameterType))))
            || (action is not null && action.ControllerTypeInfo.DeclaredConstructors.Any(constructor =>
                constructor.GetParameters().Any(parameter => UsesGalleryContract(parameter.ParameterType))));
    }

    private static bool IdentifiesGallery(string? value) =>
        value is not null && (value.Contains("gallery", StringComparison.OrdinalIgnoreCase)
            || (value.Contains("hobbies", StringComparison.OrdinalIgnoreCase)
                && new[] { "photo", "cover", "media", "signed" }.Any(term =>
                    value.Contains(term, StringComparison.OrdinalIgnoreCase))));

    private static bool UsesGalleryContract(Type type) =>
        type == typeof(GalleryPhotoDto)
        || type == typeof(GalleryPrivateMediaAccess)
        || type == typeof(AccountBackedGalleryActorResolver)
        || typeof(IGalleryService).IsAssignableFrom(type)
        || typeof(IGalleryRepository).IsAssignableFrom(type)
        || typeof(IGalleryPrivateMediaAccessReader).IsAssignableFrom(type)
        || (type.IsArray && UsesGalleryContract(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(UsesGalleryContract));

    private sealed class TestKeyRepository : IXmlRepository
    {
        private readonly ConcurrentQueue<XElement> _elements = new();

        public IReadOnlyCollection<XElement> GetAllElements() =>
            _elements.Select(element => new XElement(element)).ToArray();

        public void StoreElement(XElement element, string friendlyName) =>
            _elements.Enqueue(new XElement(element));
    }
}
