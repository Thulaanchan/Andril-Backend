using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Andril.API.Controllers.Common;
using Andril.Economy.Payments;
using Andril.Economy.Premium;
using Andril.SecurityTests.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Authorization;

public sealed class Member2DraftRouteIsolationTests
{
    [Fact]
    public async Task AuthenticatedCallerCannotReachDraftRoutesAndDraftWritersAreNotRegistered()
    {
        using var authentication = new AuthenticationFactory();
        using var host = authentication.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                [new("ConnectionStrings:AndrilAccounts", ""), new("ConnectionStrings:AndrilEconomy", "")]));
            builder.ConfigureTestServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
        });
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authentication.Token());
        using var identity = await client.GetAsync("/auth-test/identity");
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        Assert.Equal(AuthenticationFactory.Subject, await identity.Content.ReadFromJsonAsync<Guid>());

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        Assert.Contains(endpoints, endpoint =>
            endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == typeof(PrivateProfilesController));

        (HttpMethod Method, string Path)[] draftRoutes =
        [
            (HttpMethod.Get, "/api/v1/account/locations"),
            (HttpMethod.Put, "/api/v1/account/profiles/social/location"),
            (HttpMethod.Get, "/api/v1/account/profiles/social/photos/additional"),
            (HttpMethod.Get, "/api/v1/account/profiles/social/display-name/availability"),
            (HttpMethod.Get, "/api/v1/account/welcome/social"),
            (HttpMethod.Get, "/api/v1/account/privacy"),
            (HttpMethod.Get, "/api/v1/account/profiles/social/setup-status"),
            (HttpMethod.Post, "/api/v1/account/profiles/social/submit"),
            (HttpMethod.Get, "/api/v1/economy/wallet"),
            (HttpMethod.Post, "/api/v1/economy/wallet/transfer"),
            (HttpMethod.Get, "/api/v1/economy/gifts/catalog"),
            (HttpMethod.Post, "/api/v1/economy/gifts/send")
        ];
        foreach (var (method, path) in draftRoutes)
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var services = host.Services.GetRequiredService<IServiceProviderIsService>();
        Assert.False(services.IsService(typeof(IEntitlementRepository)));
        Assert.False(services.IsService(typeof(IEntitlementService)));
        Assert.False(services.IsService(typeof(IPaymentVerificationService)));
    }
}
