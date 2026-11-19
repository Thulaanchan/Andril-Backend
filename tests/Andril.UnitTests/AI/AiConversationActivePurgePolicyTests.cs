using System;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// Synthetic tests for combined active-data purge deadline calculations.
/// No database, worker, authorization or physical deletion is exercised.
/// </summary>
public sealed class AiConversationActivePurgePolicyTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Target_IsTwentyFourHours()
    {
        Assert.Same(typeof(AiConversationEntity).Assembly,
            typeof(AiConversationRetentionPolicy).Assembly);
        Assert.Equal(TimeSpan.FromHours(24),
            AiConversationRetentionPolicy.ActiveDataPurgeTarget);
        Assert.Equal(TimeSpan.FromHours(24),
            AiConversationRetentionPolicy.ManualDeletionActivePurgeTarget);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void ActiveConversation_HasNoActionableTarget(AiModule module)
    {
        var row = Create(module);
        var now = row.ExpiresAtUtc.AddTicks(-1);

        Assert.Null(Target(row, now));
        Assert.False(Reached(row, now));
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void AutomaticExpiry_StartsPurgeClock(AiModule module)
    {
        var row = Create(module);
        var expiry = CreatedAt.AddDays(14);

        Assert.True(AiConversationRetentionPolicy.MustHideFromHistory(
            row, expiry));
        Assert.True(AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(
            row, expiry));
        Assert.Equal(expiry.AddHours(24), Target(row, expiry));
        Assert.False(Reached(row, expiry));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void AutomaticExpiry_ExactPurgeBoundary(int ticks, bool expected)
    {
        var row = Create();
        var due = CreatedAt.AddDays(15);

        Assert.Equal(expected, Reached(row, due.AddTicks(ticks)));
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void ManualDeletion_StartsPurgeClock(AiModule module)
    {
        var row = Create(module);
        var deleted = CreatedAt.AddHours(2);
        row.MarkDeleted(deleted);

        Assert.True(AiConversationRetentionPolicy.MustHideFromHistory(
            row, deleted));
        Assert.True(AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(
            row, deleted));
        Assert.Equal(deleted.AddHours(24), Target(row, deleted));
        Assert.False(Reached(row, deleted));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ManualDeletion_ExactPurgeBoundary(int ticks, bool expected)
    {
        var row = Create();
        var deleted = CreatedAt.AddHours(2);
        row.MarkDeleted(deleted);

        Assert.Equal(expected,
            Reached(row, deleted.AddHours(24).AddTicks(ticks)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(48)]
    public void EarliestRevocation_DeterminesDeadline(int hoursFromExpiry)
    {
        var row = Create();
        var expiry = row.ExpiresAtUtc;
        var deleted = expiry.AddHours(hoursFromExpiry);
        row.MarkDeleted(deleted);
        var earliest = deleted < expiry ? deleted : expiry;
        var now = deleted > expiry ? deleted : expiry;

        Assert.Equal(earliest.AddHours(24), Target(row, now));
        Assert.Equal(now >= earliest.AddHours(24), Reached(row, now));
    }

    [Fact]
    public void DelayedCleanupDiscovery_DoesNotResetDeadline()
    {
        var row = Create();
        var expiry = row.ExpiresAtUtc;
        var discovered = expiry.AddDays(3);

        Assert.Equal(expiry.AddHours(24), Target(row, discovered));
        Assert.True(Reached(row, discovered));
    }

    [Fact]
    public void RepeatedDeletion_DoesNotPostponeDeadline()
    {
        var row = Create();
        var deleted = CreatedAt.AddHours(1);
        row.MarkDeleted(deleted);
        row.MarkDeleted(deleted.AddHours(5));

        Assert.Equal(deleted, row.DeletedAtUtc);
        Assert.Equal(deleted.AddHours(24), Target(row, deleted.AddHours(5)));
    }

    [Fact]
    public void SavedUserMessage_ShiftsFutureExpiryAndPurgeDeadline()
    {
        var row = Create();
        var originalExpiry = row.ExpiresAtUtc;
        var saved = CreatedAt.AddDays(5);
        row.ApplySavedUserMessage(saved);

        Assert.Null(Target(row, originalExpiry));
        Assert.False(Reached(row, originalExpiry.AddHours(24)));
        Assert.Equal(saved.AddDays(15), Target(row, row.ExpiresAtUtc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonUtcClock_UsesSameAbsoluteDeadline(bool manual)
    {
        var row = Create();
        var revoked = manual ? CreatedAt.AddHours(2) : row.ExpiresAtUtc;
        var offset = TimeSpan.FromMinutes(330);
        if (manual)
            row.MarkDeleted(revoked.ToOffset(offset));

        var due = revoked.AddHours(24);
        var localDue = due.ToOffset(offset);
        var target = Target(row, localDue);

        Assert.Equal(due, target);
        Assert.Equal(TimeSpan.Zero, target!.Value.Offset);
        Assert.False(Reached(row, localDue.AddTicks(-1)));
        Assert.True(Reached(row, localDue));
    }

    [Fact]
    public void LegacyManualDeadline_RemainsCompatible()
    {
        var row = Create();
        var expiry = row.ExpiresAtUtc;
        Assert.Null(AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(row));
        Assert.Equal(expiry.AddHours(24), Target(row, expiry));

        var deleted = expiry.AddHours(2);
        row.MarkDeleted(deleted);
        Assert.Equal(deleted.AddHours(24),
            AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(row));
        Assert.Equal(expiry.AddHours(24), Target(row, deleted));
    }

    [Fact]
    public void DeadlineChecks_DoNotMutateConversation()
    {
        var row = Create();
        var lastSaved = row.LastSavedUserMessageAtUtc;
        var expiry = row.ExpiresAtUtc;
        var deleted = CreatedAt.AddHours(1);
        row.MarkDeleted(deleted);

        Target(row, deleted);
        Reached(row, deleted.AddDays(1));

        Assert.Equal(lastSaved, row.LastSavedUserMessageAtUtc);
        Assert.Equal(expiry, row.ExpiresAtUtc);
        Assert.Equal(deleted, row.DeletedAtUtc);
    }

    [Fact]
    public void NullConversation_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Target(null!, CreatedAt));
        Assert.Throws<ArgumentNullException>(() => Reached(null!, CreatedAt));
    }

    [Fact]
    public void DefaultServerClock_IsRejected()
    {
        var row = Create();
        Assert.Throws<ArgumentException>(() => Target(row, default));
        Assert.Throws<ArgumentException>(() => Reached(row, default));
    }

    [Theory]
    [InlineData("AccountId")]
    [InlineData("ExpiresAtUtc")]
    public void MalformedPersistedRow_IsRejected(string property)
    {
        var row = Create();
        object value = property == "AccountId"
            ? (object)Guid.Empty : row.ExpiresAtUtc.AddTicks(1);

        // Simulates corrupted materialized data in the test project only.
        typeof(AiConversationEntity).GetProperty(property)!
            .GetSetMethod(nonPublic: true)!.Invoke(row, new[] { value });

        Assert.Throws<InvalidOperationException>(() => Target(row, CreatedAt));
        Assert.Throws<InvalidOperationException>(() => Reached(row, CreatedAt));
    }

    private static AiConversationEntity Create(AiModule module = AiModule.Learning) =>
        AiConversationEntity.CreateForFirstUserMessage(
            new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), module),
            Guid.NewGuid(), CreatedAt);

    private static DateTimeOffset? Target(AiConversationEntity row, DateTimeOffset now) =>
        AiConversationRetentionPolicy.GetActivePurgeTargetUtc(row, now);

    private static bool Reached(AiConversationEntity row, DateTimeOffset now) =>
        AiConversationRetentionPolicy.IsActivePurgeTargetReached(row, now);
}
