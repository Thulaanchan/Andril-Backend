using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Production-policy tests with synthetic entities; no physical cleanup is executed.</summary>
public sealed class AiConversationRetentionPolicyTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RetentionTypes_OriginateInProductionAssembly()
    {
        var assembly = typeof(AiConversationEntity).Assembly;
        Assert.Equal("Andril.AI", assembly.GetName().Name);
        Assert.Same(assembly, typeof(AiConversationRetentionPolicy).Assembly);
        Assert.Same(assembly, typeof(AiConversationRetentionState).Assembly);
        Assert.NotSame(typeof(AiConversationRetentionPolicyTests).Assembly, assembly);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void Expiry_ImmediatelyHidesConversationAtFourteenDayBoundary(AiModule module)
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), module);
        var row = AiConversationEntity.CreateForFirstUserMessage(actor, Guid.NewGuid(), CreatedAt);
        var expiry = CreatedAt.AddDays(14);

        Assert.Equal(expiry, row.ExpiresAtUtc);
        Assert.Equal(AiConversationRetentionState.Active,
            AiConversationRetentionPolicy.Classify(row, expiry.AddTicks(-1)));
        Assert.False(AiConversationRetentionPolicy.MustHideFromHistory(row, expiry.AddTicks(-1)));
        Assert.False(AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(row, expiry.AddTicks(-1)));
        Assert.True(row.IsAccessibleBy(actor, expiry.AddTicks(-1)));
        Assert.Equal(AiConversationRetentionState.Expired,
            AiConversationRetentionPolicy.Classify(row, expiry));
        Assert.True(AiConversationRetentionPolicy.MustHideFromHistory(row, expiry));
        Assert.True(AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(row, expiry));
        Assert.False(row.IsAccessibleBy(actor, expiry));
        Assert.Equal(expiry.AddHours(24), AiConversationRetentionPolicy.GetActivePurgeTargetUtc(row, expiry));
    }

    [Fact]
    public void ManualDeletion_ImmediatelyDeniesAccessAndStartsPurgeClock()
    {
        var row = Create();
        var actor = new AiAuthorizedActor(row.AccountId, row.ModuleProfileId, row.Module);
        var deleted = CreatedAt.AddHours(1);
        row.MarkDeleted(deleted);
        Assert.Equal(AiConversationRetentionState.Deleted,
            AiConversationRetentionPolicy.Classify(row, deleted));
        Assert.True(AiConversationRetentionPolicy.MustHideFromHistory(row, deleted));
        Assert.True(AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(row, deleted));
        Assert.False(row.IsAccessibleBy(actor, deleted));
        Assert.Equal(deleted.AddHours(24), AiConversationRetentionPolicy.GetActivePurgeTargetUtc(row, deleted));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void DeletedClassification_PreservesEarliestRevocationDeadline(int hoursFromExpiry)
    {
        var row = Create();
        var expiry = row.ExpiresAtUtc;
        var deleted = expiry.AddHours(hoursFromExpiry);
        row.MarkDeleted(deleted);
        var now = deleted > expiry ? deleted : expiry;
        var due = hoursFromExpiry < 0 ? deleted.AddHours(24) : expiry.AddHours(24);
        Assert.Equal(AiConversationRetentionState.Deleted, AiConversationRetentionPolicy.Classify(row, now));
        Assert.Equal(due, AiConversationRetentionPolicy.GetActivePurgeTargetUtc(row, now));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ManualPurgeDeadline_IsInclusive(int ticks, bool reached)
    {
        var row = Create();
        var deleted = CreatedAt.AddHours(1);
        row.MarkDeleted(deleted);
        var due = deleted.AddHours(24);
        Assert.Equal(due, AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(row));
        Assert.Equal(reached, AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(row, due.AddTicks(ticks)));
        Assert.Equal(reached, AiConversationRetentionPolicy.IsActivePurgeTargetReached(row, due.AddTicks(ticks)));
    }

    [Fact]
    public void ActiveConversation_HasNoPurgeTarget()
    {
        var row = Create();
        Assert.Null(AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(row));
        Assert.Null(AiConversationRetentionPolicy.GetActivePurgeTargetUtc(row, CreatedAt));
        Assert.False(AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(row, CreatedAt));
        Assert.False(AiConversationRetentionPolicy.IsActivePurgeTargetReached(row, CreatedAt));
    }

    [Fact]
    public void RepeatedEvaluation_DoesNotExtendSavedUserAnchorOrPurgeDeadline()
    {
        var row = Create();
        var saved = CreatedAt.AddDays(2);
        row.ApplySavedUserMessage(saved);
        var expiry = saved.AddDays(14);
        foreach (var now in new[] { expiry, expiry.AddHours(24), expiry.AddDays(7) })
        {
            Assert.Equal(AiConversationRetentionState.Expired, AiConversationRetentionPolicy.Classify(row, now));
            Assert.Equal(expiry.AddHours(24), AiConversationRetentionPolicy.GetActivePurgeTargetUtc(row, now));
            Assert.Equal(saved, row.LastSavedUserMessageAtUtc);
            Assert.Equal(expiry, row.ExpiresAtUtc);
            Assert.Null(row.DeletedAtUtc);
        }
    }

    [Fact]
    public void OffsetClock_UsesSameInstantAndReturnsUtcDeadline()
    {
        var row = Create();
        var localExpiry = row.ExpiresAtUtc.ToOffset(TimeSpan.FromMinutes(330));
        Assert.Equal(AiConversationRetentionState.Expired, AiConversationRetentionPolicy.Classify(row, localExpiry));
        var due = AiConversationRetentionPolicy.GetActivePurgeTargetUtc(row, localExpiry);
        Assert.Equal(row.ExpiresAtUtc.AddHours(24), due);
        Assert.Equal(TimeSpan.Zero, due!.Value.Offset);
    }

    [Fact]
    public void NullConversation_IsRejectedByEveryEntryPoint()
    {
        Assert.Throws<ArgumentNullException>(() => AiConversationRetentionPolicy.Classify(null!, CreatedAt));
        Assert.Throws<ArgumentNullException>(() => AiConversationRetentionPolicy.MustHideFromHistory(null!, CreatedAt));
        Assert.Throws<ArgumentNullException>(() => AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(null!, CreatedAt));
        Assert.Throws<ArgumentNullException>(() => AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(null!));
        Assert.Throws<ArgumentNullException>(() => AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(null!, CreatedAt));
        Assert.Throws<ArgumentNullException>(() => AiConversationRetentionPolicy.GetActivePurgeTargetUtc(null!, CreatedAt));
        Assert.Throws<ArgumentNullException>(() => AiConversationRetentionPolicy.IsActivePurgeTargetReached(null!, CreatedAt));
    }

    [Fact]
    public void DefaultClock_IsRejectedByTimedEntryPoints()
    {
        var row = Create();
        Assert.Throws<ArgumentException>(() => AiConversationRetentionPolicy.Classify(row, default));
        Assert.Throws<ArgumentException>(() => AiConversationRetentionPolicy.MustHideFromHistory(row, default));
        Assert.Throws<ArgumentException>(() => AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(row, default));
        Assert.Throws<ArgumentException>(() => AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(row, default));
        Assert.Throws<ArgumentException>(() => AiConversationRetentionPolicy.GetActivePurgeTargetUtc(row, default));
        Assert.Throws<ArgumentException>(() => AiConversationRetentionPolicy.IsActivePurgeTargetReached(row, default));
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("AccountId")]
    [InlineData("ModuleProfileId")]
    [InlineData("CreateOperationId")]
    [InlineData("Module")]
    [InlineData("CreatedAtUtc")]
    [InlineData("CreatedOffset")]
    [InlineData("LastSavedUserMessageAtUtc")]
    [InlineData("SavedOffset")]
    [InlineData("ExpiresAtUtc")]
    [InlineData("ExpiryOffset")]
    [InlineData("DeletedAtUtc")]
    [InlineData("DeletedOffset")]
    public void MalformedMaterializedState_IsRejected(string defect)
    {
        var row = Create();
        var property = defect;
        object value = Guid.Empty;
        switch (defect)
        {
            case "Module": value = (AiModule)99; break;
            case "CreatedAtUtc": value = default(DateTimeOffset); break;
            case "CreatedOffset": property = "CreatedAtUtc"; value = CreatedAt.ToOffset(TimeSpan.FromHours(1)); break;
            case "LastSavedUserMessageAtUtc": value = CreatedAt.AddTicks(-1); break;
            case "SavedOffset": property = "LastSavedUserMessageAtUtc"; value = CreatedAt.ToOffset(TimeSpan.FromHours(1)); break;
            case "ExpiresAtUtc": value = row.ExpiresAtUtc.AddTicks(1); break;
            case "ExpiryOffset": property = "ExpiresAtUtc"; value = row.ExpiresAtUtc.ToOffset(TimeSpan.FromHours(1)); break;
            case "DeletedAtUtc": value = CreatedAt.AddTicks(-1); break;
            case "DeletedOffset": property = "DeletedAtUtc"; value = CreatedAt.ToOffset(TimeSpan.FromHours(1)); break;
        }
        // Reflection only simulates corrupted ORM state, not production-policy resolution.
        typeof(AiConversationEntity).GetProperty(property)!
            .GetSetMethod(nonPublic: true)!.Invoke(row, new[] { value });
        Assert.Throws<InvalidOperationException>(() => AiConversationRetentionPolicy.Classify(row, CreatedAt));
        Assert.Throws<InvalidOperationException>(() => AiConversationRetentionPolicy.MustHideFromHistory(row, CreatedAt));
        Assert.Throws<InvalidOperationException>(() => AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(row, CreatedAt));
        Assert.Throws<InvalidOperationException>(() => AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(row));
        Assert.Throws<InvalidOperationException>(() => AiConversationRetentionPolicy.GetActivePurgeTargetUtc(row, CreatedAt));
    }

    private static AiConversationEntity Create() =>
        AiConversationEntity.CreateForFirstUserMessage(
            new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning),
            Guid.NewGuid(), CreatedAt);
}
