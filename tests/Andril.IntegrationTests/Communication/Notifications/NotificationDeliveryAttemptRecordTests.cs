using System;
using System.Linq;
using System.Reflection;
using Andril.Persistence.DbContexts;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

/// <summary>
/// D14-B offline contract tests for the dormant internal candidate.
/// Reflection avoids changing production assembly visibility or API contracts.
/// No DbContext is constructed and no database or provider is accessed.
/// </summary>
public sealed class NotificationDeliveryAttemptRecordTests
{
    private static readonly Assembly PersistenceAssembly =
        typeof(NotificationsDbContext).Assembly;

    private static readonly Type RecordType =
        PersistenceAssembly.GetType(
            "Andril.Persistence.Repositories.Communication.NotificationDeliveryAttemptRecord",
            throwOnError: true)!;

    private static readonly Type OutcomeType =
        PersistenceAssembly.GetType(
            "Andril.Persistence.Repositories.Communication.NotificationDeliveryAttemptOutcome",
            throwOnError: true)!;

    private static readonly MethodInfo CreateMethod =
        RecordType.GetMethod("CreateClaimed", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo CompleteMethod =
        RecordType.GetMethod("TryRecordOutcome", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly DateTimeOffset ClaimedAt =
        new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static object NewRecord(
        string module = "learning",
        Guid? attemptId = null,
        Guid? accountId = null,
        DateTimeOffset? claimedAt = null)
    {
        return CreateMethod.Invoke(null, new object?[]
        {
            attemptId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            accountId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            module,
            Guid.NewGuid(),
            claimedAt ?? ClaimedAt
        })!;
    }

    private static object State(string value) => Enum.Parse(OutcomeType, value);

    private static T Read<T>(object record, string property) =>
        (T)RecordType.GetProperty(property)!.GetValue(record)!;

    private static bool Complete(object record, string outcome, DateTimeOffset? at = null) =>
        (bool)CompleteMethod.Invoke(record, new[] { State(outcome), (object)(at ?? ClaimedAt.AddSeconds(1)) })!;

    [Fact]
    public void CreateClaimed_PreservesScopedIdentifiersAndStartsUncompleted()
    {
        var record = NewRecord();

        Assert.NotEqual(Guid.Empty, Read<Guid>(record, "Id"));
        Assert.NotEqual(Guid.Empty, Read<Guid>(record, "NotificationId"));
        Assert.NotEqual(Guid.Empty, Read<Guid>(record, "RecipientAccountId"));
        Assert.NotEqual(Guid.Empty, Read<Guid>(record, "RecipientModuleProfileId"));
        Assert.NotEqual(Guid.Empty, Read<Guid>(record, "DeviceRegistrationId"));
        Assert.Equal("learning", Read<string>(record, "Module"));
        Assert.Equal("Claimed", Read<object>(record, "Outcome").ToString());
        Assert.Null(Read<DateTimeOffset?>(record, "CompletedAtUtc"));
    }

    [Fact]
    public void RejectsUnrecognizedModule_WithoutCreatingRecord()
    {
        foreach (var module in new[] { "dating", "Learning", "social ", "", "not-a-module" })
        {
            var exception = Assert.Throws<TargetInvocationException>(() => NewRecord(module));
            Assert.IsType<ArgumentException>(exception.InnerException);
        }
    }

    [Fact]
    public void RejectsEmptyAttemptAndAccountIds()
    {
        var emptyAttempt = Assert.Throws<TargetInvocationException>(() =>
            NewRecord(attemptId: Guid.Empty));
        Assert.IsType<ArgumentException>(emptyAttempt.InnerException);

        var emptyAccount = Assert.Throws<TargetInvocationException>(() =>
            NewRecord(accountId: Guid.Empty));
        Assert.IsType<ArgumentException>(emptyAccount.InnerException);
    }

    [Fact]
    public void ClaimedTimestamp_IsNormalizedToUtc()
    {
        var input = new DateTimeOffset(2026, 11, 22, 15, 30, 0, TimeSpan.FromMinutes(330));
        var record = NewRecord(claimedAt: input);

        Assert.Equal(ClaimedAt, Read<DateTimeOffset>(record, "ClaimedAtUtc"));
        Assert.Equal(TimeSpan.Zero, Read<DateTimeOffset>(record, "ClaimedAtUtc").Offset);
    }

    [Fact]
    public void ProviderAccepted_FinalizesOneAttemptWithoutResending()
    {
        var record = NewRecord();
        Assert.True(Complete(record, "ProviderAccepted"));
        Assert.False(Complete(record, "ProviderAccepted"));
        Assert.Equal("ProviderAccepted", Read<object>(record, "Outcome").ToString());
        Assert.Equal(ClaimedAt.AddSeconds(1), Read<DateTimeOffset?>(record, "CompletedAtUtc"));
    }

    [Fact]
    public void UnknownOutcome_CannotBeSilentlyOverwritten()
    {
        var record = NewRecord(module: "social");
        Assert.True(Complete(record, "Unknown"));
        Assert.False(Complete(record, "ProviderAccepted"));
        Assert.Equal("Unknown", Read<object>(record, "Outcome").ToString());
    }

    [Fact]
    public void InvalidOutcomeAndEarlierTimestamp_LeaveClaimUnchanged()
    {
        var record = NewRecord();
        var invalid = Assert.Throws<TargetInvocationException>(() => Complete(record, "Claimed"));
        Assert.IsType<ArgumentOutOfRangeException>(invalid.InnerException);

        var earlier = Assert.Throws<TargetInvocationException>(() =>
            Complete(record, "TemporaryFailure", ClaimedAt.AddMinutes(-1)));
        Assert.IsType<ArgumentOutOfRangeException>(earlier.InnerException);

        Assert.Equal("Claimed", Read<object>(record, "Outcome").ToString());
        Assert.Null(Read<DateTimeOffset?>(record, "CompletedAtUtc"));
    }

    [Fact]
    public void Record_DoesNotContainSensitivePayloadOrProviderTokenFields()
    {
        var propertyNames = RecordType.GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToArray();

        foreach (var sensitive in new[]
        {
            "DeviceToken", "PushToken", "ProfileName", "DisplayName",
            "MessagePreview", "Payload", "ProviderCredential", "ProviderError"
        })
        {
            Assert.DoesNotContain(sensitive, propertyNames);
        }
    }

    [Fact]
    public void CreateClaimed_RejectsEmptyNotificationProfileAndDeviceIds()
    {
        foreach (var index in new[] { 1, 3, 5 })
        {
            object?[] args =
            {
            Guid.NewGuid(), // Attempt ID
            Guid.NewGuid(), // Notification ID
            Guid.NewGuid(), // Account ID
            Guid.NewGuid(), // Private profile ID
            "learning",
            Guid.NewGuid(), // Device registration ID
            ClaimedAt
        };

            args[index] = Guid.Empty;

            var exception =
                Assert.Throws<TargetInvocationException>(() =>
                    CreateMethod.Invoke(null, args));

            Assert.IsType<ArgumentException>(
                exception.InnerException);
        }
    }

    [Fact]
    public void InvalidOutcomeAndDefaultTime_ShouldPreserveClaim()
    {
        var record = NewRecord();

        var invalidOutcome = Enum.ToObject(OutcomeType, 999);

        var invalidException =
            Assert.Throws<TargetInvocationException>(() =>
                CompleteMethod.Invoke(
                    record,
                    new object?[]
                    {
                    invalidOutcome,
                    ClaimedAt.AddSeconds(1)
                    }));

        Assert.IsType<ArgumentOutOfRangeException>(
            invalidException.InnerException);

        var timeException =
            Assert.Throws<TargetInvocationException>(() =>
                CompleteMethod.Invoke(
                    record,
                    new object?[]
                    {
                    State("TemporaryFailure"),
                    default(DateTimeOffset)
                    }));

        Assert.IsType<ArgumentOutOfRangeException>(
            timeException.InnerException);

        Assert.Equal(
            "Claimed",
            Read<object>(record, "Outcome").ToString());

        Assert.Null(
            Read<DateTimeOffset?>(record, "CompletedAtUtc"));
    }

}
