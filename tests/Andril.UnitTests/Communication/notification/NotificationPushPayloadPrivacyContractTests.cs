
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// D7-C2 offline privacy regression tests.
///
/// Protects the minimal push request contract from
/// accidental exposure of notification content.
///
/// No provider, database, or network connection.
/// </summary>
public sealed class NotificationPushPayloadPrivacyContractTests
{
    [Fact]
    public void PushRequest_ShouldExposeOnlyApprovedProperties()
    {
        var actual = typeof(NotificationPushRequest)
            .GetProperties(
                BindingFlags.Public |
                BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var expected = new[]
        {
            nameof(NotificationPushRequest.DeviceRegistrationId),
            nameof(NotificationPushRequest.NotificationId),
            nameof(NotificationPushRequest.RecipientScope)
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PushRequest_ShouldNotExposePublicFields()
    {
        var fields = typeof(NotificationPushRequest)
            .GetFields(
                BindingFlags.Public |
                BindingFlags.Instance);

        Assert.Empty(fields);
    }

    [Fact]
    public void PushRequest_ShouldAcceptOnlyApprovedConstructorData()
    {
        var constructor = Assert.Single(
            typeof(NotificationPushRequest).GetConstructors(
                BindingFlags.Public |
                BindingFlags.Instance));

        var parameterTypes = constructor
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            new[]
            {
                typeof(Guid),
                typeof(NotificationModuleScope),
                typeof(Guid)
            },
            parameterTypes);
    }

    [Fact]
    public void PushSender_ShouldAcceptOnlyMinimalRequest()
    {
            var method = Assert.Single(
                typeof(INotificationPushSender).GetMethods(),
                method => method.Name ==
                    nameof(INotificationPushSender.SendAsync));

            Assert.Equal(
                typeof(Task<NotificationPushResult>),
                method.ReturnType);

            var parameters = method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray();

            Assert.Equal(
                new[]
                {
            typeof(NotificationPushRequest),
            typeof(CancellationToken)
                },
                parameters);
    }
}
