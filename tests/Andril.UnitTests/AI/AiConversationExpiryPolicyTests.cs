
using System;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// AI-BE-03 tests for the pure 14-day inactivity calculation.
/// Uses fixed timestamps only; no database, API, or real user data.
/// </summary>
public sealed class AiConversationExpiryPolicyTests
{
    private static readonly DateTimeOffset LastSavedUserMessageUtc =
        new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InactivityWindow_IsExactlyFourteenDays()
    {
        Assert.Equal(TimeSpan.FromDays(14),
            AiConversationExpiryPolicy.InactivityWindow);
    }

    [Fact]
    public void GetExpiresAtUtc_AddsFourteenDaysToSavedUserMessage()
    {
        var expiresAt = AiConversationExpiryPolicy.GetExpiresAtUtc(
            LastSavedUserMessageUtc);

        Assert.Equal(
            new DateTimeOffset(2026, 11, 30, 10, 0, 0, TimeSpan.Zero),
            expiresAt);
    }

    [Fact]
    public void GetExpiresAtUtc_ConvertsOffsetTimestampToUtc()
    {
        var savedInSriLanka = new DateTimeOffset(
            2026, 11, 16, 15, 30, 0, TimeSpan.FromMinutes(330));

        var expiresAt = AiConversationExpiryPolicy.GetExpiresAtUtc(
            savedInSriLanka);

        Assert.Equal(
            new DateTimeOffset(2026, 11, 30, 10, 0, 0, TimeSpan.Zero),
            expiresAt);
        Assert.Equal(TimeSpan.Zero, expiresAt.Offset);
    }

    [Fact]
    public void IsExpired_JustBeforeDeadline_ReturnsFalse()
    {
        var deadline = AiConversationExpiryPolicy.GetExpiresAtUtc(
            LastSavedUserMessageUtc);

        Assert.False(AiConversationExpiryPolicy.IsExpired(
            LastSavedUserMessageUtc, deadline.AddTicks(-1)));
    }

    [Fact]
    public void IsExpired_ExactlyAtDeadline_ReturnsTrue()
    {
        var deadline = AiConversationExpiryPolicy.GetExpiresAtUtc(
            LastSavedUserMessageUtc);

        Assert.True(AiConversationExpiryPolicy.IsExpired(
            LastSavedUserMessageUtc, deadline));
    }

    [Fact]
    public void IsExpired_AfterDeadline_ReturnsTrue()
    {
        var deadline = AiConversationExpiryPolicy.GetExpiresAtUtc(
            LastSavedUserMessageUtc);

        Assert.True(AiConversationExpiryPolicy.IsExpired(
            LastSavedUserMessageUtc, deadline.AddSeconds(1)));
    }

    [Fact]
    public void IsExpired_NormalizesCurrentTimeWithNonUtcOffset()
    {
        var sameDeadlineInSriLanka = new DateTimeOffset(
            2026, 11, 30, 15, 30, 0, TimeSpan.FromMinutes(330));

        Assert.True(AiConversationExpiryPolicy.IsExpired(
            LastSavedUserMessageUtc, sameDeadlineInSriLanka));
    }

    [Fact]
    public void LaterSuccessfullySavedUserMessage_RenewsTheDeadline()
    {
        var newSavedUserMessage = LastSavedUserMessageUtc.AddDays(5);
        var oldDeadline = AiConversationExpiryPolicy.GetExpiresAtUtc(
            LastSavedUserMessageUtc);
        var newDeadline = AiConversationExpiryPolicy.GetExpiresAtUtc(
            newSavedUserMessage);

        Assert.Equal(oldDeadline.AddDays(5), newDeadline);
        Assert.True(AiConversationExpiryPolicy.IsExpired(
            LastSavedUserMessageUtc, oldDeadline));
        Assert.False(AiConversationExpiryPolicy.IsExpired(
            newSavedUserMessage, oldDeadline));
    }

    [Fact]
    public void UnchangedLastSavedUserMessage_DoesNotRenewExpiry()
    {
        // Read/resume/assistant-message events must not replace this timestamp.
        // Persistence-layer enforcement needs separate integration tests.
        var firstCheck = AiConversationExpiryPolicy.GetExpiresAtUtc(
            LastSavedUserMessageUtc);
        var afterReadOrAssistantReply =
            AiConversationExpiryPolicy.GetExpiresAtUtc(
                LastSavedUserMessageUtc);

        Assert.Equal(firstCheck, afterReadOrAssistantReply);
    }

    [Fact]
    public void GetExpiresAtUtc_DefaultTimestamp_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            AiConversationExpiryPolicy.GetExpiresAtUtc(default));

        Assert.Equal("lastSavedUserMessageAtUtc", error.ParamName);
    }

    [Fact]
    public void IsExpired_DefaultTimestamp_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AiConversationExpiryPolicy.IsExpired(
                default, LastSavedUserMessageUtc));
    }
}
