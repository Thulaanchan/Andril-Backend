
using System;
using System.Text.RegularExpressions;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

// Test-only naming policy.
// No database connection is opened.
internal static class NotificationTestDatabaseNamePolicy
{
    private const string Prefix =
        "andril_m4_notification_test_";

    private const string Pattern =
        "^andril_m4_notification_test_[0-9a-f]{32}$";

    internal static string CreateNewName()
    {
        return Prefix + Guid.NewGuid().ToString("N");
    }

    internal static bool IsValidName(string? name)
    {
        return name is not null
            && name.Length <= 63
            && Regex.IsMatch(
                name,
                Pattern,
                RegexOptions.CultureInvariant);
    }

    // Exact name matching is necessary for cleanup,
    // but not sufficient without proof that this
    // fixture actually created the database.
    internal static bool MatchesCreatedName(
        string? requestedName,
        string? createdName)
    {
        return IsValidName(requestedName)
            && IsValidName(createdName)
            && string.Equals(
                requestedName,
                createdName,
                StringComparison.Ordinal);
    }
}

public sealed class NotificationTestDatabaseNamePolicyTests
{
    [Fact]
    public void GeneratedName_ShouldBeValid()
    {
        var name =
            NotificationTestDatabaseNamePolicy.CreateNewName();

        Assert.True(
            NotificationTestDatabaseNamePolicy.IsValidName(name));

        Assert.True(name.Length <= 63);
    }

    [Fact]
    public void GeneratedNames_ShouldBeUnique()
    {
        var first =
            NotificationTestDatabaseNamePolicy.CreateNewName();

        var second =
            NotificationTestDatabaseNamePolicy.CreateNewName();

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("postgres")]
    [InlineData("production")]
    [InlineData("andril_m4_notification_test_")]
    [InlineData("andril_m4_notification_test_invalid")]
    [InlineData("andril_m4_notification_test_abc;DROP DATABASE")]
    public void InvalidNames_ShouldBeRejected(string? name)
    {
        Assert.False(
            NotificationTestDatabaseNamePolicy.IsValidName(name));
    }

    [Fact]
    public void ExactCreatedName_ShouldMatch()
    {
        var created =
            NotificationTestDatabaseNamePolicy.CreateNewName();

        Assert.True(
            NotificationTestDatabaseNamePolicy.MatchesCreatedName(
                created,
                created));
    }

    [Fact]
    public void DifferentDatabase_ShouldNotMatch()
    {
        var created =
            NotificationTestDatabaseNamePolicy.CreateNewName();

        var other =
            NotificationTestDatabaseNamePolicy.CreateNewName();

        Assert.False(
            NotificationTestDatabaseNamePolicy.MatchesCreatedName(
                other,
                created));
    }

    [Fact]
    public void MissingCreatedName_ShouldNotMatch()
    {
        var requested =
            NotificationTestDatabaseNamePolicy.CreateNewName();

        Assert.False(
            NotificationTestDatabaseNamePolicy.MatchesCreatedName(
                requested,
                null));
    }
}
