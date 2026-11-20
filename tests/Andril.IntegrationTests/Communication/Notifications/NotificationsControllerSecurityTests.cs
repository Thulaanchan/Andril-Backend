
using System;
using System.Linq;
using System.Reflection;
using Andril.API.Controllers.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

public sealed class NotificationsControllerSecurityTests
{
    [Fact]
    public void Controller_ShouldRequireAuthorization()
    {
        var controller = typeof(NotificationsController);

        Assert.NotNull(
            controller.GetCustomAttribute<AuthorizeAttribute>());

        Assert.NotNull(
            controller.GetCustomAttribute<ApiControllerAttribute>());
    }

    [Fact]
    public void Controller_ShouldDisableResponseCaching()
    {
        var controller = typeof(NotificationsController);

        var cache = controller
            .GetCustomAttribute<ResponseCacheAttribute>();

        Assert.NotNull(cache);
        Assert.True(cache.NoStore);

        Assert.Equal(
            ResponseCacheLocation.None,
            cache.Location);
    }

    [Fact]
    public void Controller_ShouldNotAllowAnonymousAccess()
    {
        var controller = typeof(NotificationsController);

        Assert.Null(
            controller.GetCustomAttribute<AllowAnonymousAttribute>());

        foreach (var method in controller.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly))
        {
            Assert.Null(
                method.GetCustomAttribute<AllowAnonymousAttribute>());
        }
    }

    [Theory]
    [InlineData("List", "{module}", "GET")]
    [InlineData("Get", "{module}/{notificationId:guid}", "GET")]
    [InlineData("MarkRead", "{module}/{notificationId:guid}/read", "PATCH")]
    public void Controller_ShouldUseExpectedRoutes(
        string methodName,
        string expectedTemplate,
        string expectedHttpMethod)
    {
        var controller = typeof(NotificationsController);

        var baseRoute = controller
            .GetCustomAttribute<RouteAttribute>();

        Assert.NotNull(baseRoute);

        Assert.Equal(
            "api/v1/notifications",
            baseRoute.Template);

        var method = controller.GetMethod(methodName);

        Assert.NotNull(method);

        var httpMethod = Assert.Single(
            method.GetCustomAttributes<HttpMethodAttribute>());

        Assert.Equal(
            expectedTemplate,
            httpMethod.Template);

        Assert.Contains(
            expectedHttpMethod,
            httpMethod.HttpMethods);
    }
}
