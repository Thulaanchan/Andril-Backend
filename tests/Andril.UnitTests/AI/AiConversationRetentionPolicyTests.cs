
using System;
using System.Reflection;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// AI-BE-03 Step 15: fixed-time, synthetic-data retention-policy tests.
/// These tests exercise pure classification and deadline calculations only.
/// They do not execute a purge, establish authorization, or verify backups.
/// </summary>
public sealed class AiConversationRetentionPolicyTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpiresAtUtc =
        CreatedAtUtc.AddDays(14);

    [Fact]
    public void ManualDeletionPurgeTarget_IsExactlyTwentyFourHours()
    {
        Assert.Equal(TimeSpan.FromHours(24),
            AiConversationRetentionPolicy.ManualDeletionActivePurgeTarget);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void Classify_NewConversationIsActiveInEachModule(AiModule module)
    {
        var conversation = Create(module);

        Assert.Equal(AiConversationRetentionState.Active,
            AiConversationRetentionPolicy.Classify(
                conversation, CreatedAtUtc));
    }

    [Fact]
    public void Classify_OneTickBeforeExpiry_IsActive()
    {
        Assert.Equal(AiConversationRetentionState.Active,
            AiConversationRetentionPolicy.Classify(
                Create(), ExpiresAtUtc.AddTicks(-1)));
    }

    [Fact]
    public void Classify_AtExactExpiry_IsExpired()
    {
        Assert.Equal(AiConversationRetentionState.Expired,
            AiConversationRetentionPolicy.Classify(
                Create(), ExpiresAtUtc));
    }

    [Fact]
    public void Classify_AfterExpiry_IsExpired()
    {
        Assert.Equal(AiConversationRetentionState.Expired,
            AiConversationRetentionPolicy.Classify(
                Create(), ExpiresAtUtc.AddTicks(1)));
    }

    [Fact]
    public void Classify_NormalizesSriLankaClockOffset()
    {
        var conversation = Create();
        var localExpiry = ExpiresAtUtc.ToOffset(
            TimeSpan.FromMinutes(330));

        Assert.Equal(AiConversationRetentionState.Active,
            AiConversationRetentionPolicy.Classify(
                conversation, localExpiry.AddTicks(-1)));
        Assert.Equal(AiConversationRetentionState.Expired,
            AiConversationRetentionPolicy.Classify(
                conversation, localExpiry));
    }

    [Fact]
    public void Classify_NewSavedUserMessage_RenewsExpiry()
    {
        var conversation = Create();
        var savedAgain = CreatedAtUtc.AddDays(5);
        conversation.ApplySavedUserMessage(savedAgain);

        Assert.Equal(savedAgain.AddDays(14), conversation.ExpiresAtUtc);
        Assert.Equal(AiConversationRetentionState.Active,
            AiConversationRetentionPolicy.Classify(
                conversation, ExpiresAtUtc));
        Assert.Equal(AiConversationRetentionState.Expired,
            AiConversationRetentionPolicy.Classify(
                conversation, savedAgain.AddDays(14)));
    }

    [Fact]
    public void Classify_DeletedBeforeExpiry_IsDeleted()
    {
        var conversation = Create();
        conversation.MarkDeleted(CreatedAtUtc.AddHours(1));

        Assert.Equal(AiConversationRetentionState.Deleted,
            AiConversationRetentionPolicy.Classify(
                conversation, CreatedAtUtc.AddHours(2)));
    }

    [Fact]
    public void Classify_DeletedAfterExpiry_TakesPriorityOverExpired()
    {
        var conversation = Create();
        conversation.MarkDeleted(ExpiresAtUtc.AddDays(1));

        Assert.Equal(AiConversationRetentionState.Deleted,
            AiConversationRetentionPolicy.Classify(
                conversation, ExpiresAtUtc.AddDays(2)));
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void Classify_DeletionPriorityIsIndependentOfModule(AiModule module)
    {
        var conversation = Create(module);
        conversation.MarkDeleted(CreatedAtUtc.AddMinutes(1));

        Assert.Equal(AiConversationRetentionState.Deleted,
            AiConversationRetentionPolicy.Classify(
                conversation, ExpiresAtUtc.AddDays(1)));
    }

    [Fact]
    public void ActiveConversation_IsNeitherHiddenNorMarkedForPurgeEvaluation()
    {
        var conversation = Create();

        Assert.False(AiConversationRetentionPolicy.MustHideFromHistory(
            conversation, CreatedAtUtc.AddDays(1)));
        Assert.False(AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(
            conversation, CreatedAtUtc.AddDays(1)));
    }

    [Fact]
    public void ExpiredConversation_IsHiddenAndMarkedForPurgeEvaluation()
    {
        var conversation = Create();

        Assert.True(AiConversationRetentionPolicy.MustHideFromHistory(
            conversation, ExpiresAtUtc));
        Assert.True(AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(
            conversation, ExpiresAtUtc));
    }

    [Fact]
    public void DeletedConversation_IsHiddenAndMarkedForPurgeEvaluation()
    {
        var conversation = Create();
        conversation.MarkDeleted(CreatedAtUtc.AddHours(1));

        Assert.True(AiConversationRetentionPolicy.MustHideFromHistory(
            conversation, CreatedAtUtc.AddHours(1)));
        Assert.True(AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(
            conversation, CreatedAtUtc.AddHours(1)));
    }

    [Fact]
    public void RetentionQueries_DoNotChangeConversationTimestamps()
    {
        var conversation = Create();
        var id = conversation.Id;
        var created = conversation.CreatedAtUtc;
        var lastSaved = conversation.LastSavedUserMessageAtUtc;
        var expiry = conversation.ExpiresAtUtc;

        AiConversationRetentionPolicy.Classify(conversation, CreatedAtUtc);
        AiConversationRetentionPolicy.MustHideFromHistory(conversation, ExpiresAtUtc);
        AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(
            conversation, ExpiresAtUtc);
        AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(
            conversation);
        AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
            conversation, ExpiresAtUtc);

        Assert.Equal(id, conversation.Id);
        Assert.Equal(created, conversation.CreatedAtUtc);
        Assert.Equal(lastSaved, conversation.LastSavedUserMessageAtUtc);
        Assert.Equal(expiry, conversation.ExpiresAtUtc);
        Assert.Null(conversation.DeletedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void GetPurgeTarget_NoManualDeletion_ReturnsNullEvenIfExpired(
        int daysAfterCreation)
    {
        var conversation = Create();

        Assert.Null(
            AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(
                conversation));
        Assert.False(
            AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
                conversation, CreatedAtUtc.AddDays(daysAfterCreation)));
    }

    [Fact]
    public void GetPurgeTarget_ManualDeletion_AddsTwentyFourHours()
    {
        var conversation = Create();
        var deletedAt = CreatedAtUtc.AddHours(2);
        conversation.MarkDeleted(deletedAt);

        Assert.Equal(deletedAt.AddHours(24),
            AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(
                conversation));
    }

    [Fact]
    public void GetPurgeTarget_DeletionOffsetIsStoredAndReturnedInUtc()
    {
        var conversation = Create();
        var localDeletedAt = CreatedAtUtc.AddHours(2).ToOffset(
            TimeSpan.FromMinutes(330));
        conversation.MarkDeleted(localDeletedAt);

        var target = AiConversationRetentionPolicy
            .GetManualDeletionPurgeTargetUtc(conversation);

        Assert.Equal(CreatedAtUtc.AddHours(26), target);
        Assert.Equal(TimeSpan.Zero, target!.Value.Offset);
    }

    [Fact]
    public void GetPurgeTarget_RepeatedDeletionKeepsOriginalDeadline()
    {
        var conversation = Create();
        var first = CreatedAtUtc.AddHours(1);
        conversation.MarkDeleted(first);
        var originalTarget = AiConversationRetentionPolicy
            .GetManualDeletionPurgeTargetUtc(conversation);

        conversation.MarkDeleted(first.AddHours(10));

        Assert.Equal(first, conversation.DeletedAtUtc);
        Assert.Equal(originalTarget,
            AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(
                conversation));
    }

    [Fact]
    public void IsPurgeTargetReached_OneTickBeforeDeadline_IsFalse()
    {
        var conversation = DeletedAt(CreatedAtUtc.AddHours(1));
        var due = CreatedAtUtc.AddHours(25);

        Assert.False(
            AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
                conversation, due.AddTicks(-1)));
    }

    [Fact]
    public void IsPurgeTargetReached_ExactlyAtDeadline_IsTrue()
    {
        var conversation = DeletedAt(CreatedAtUtc.AddHours(1));

        Assert.True(
            AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
                conversation, CreatedAtUtc.AddHours(25)));
    }

    [Fact]
    public void IsPurgeTargetReached_AfterDeadline_IsTrue()
    {
        var conversation = DeletedAt(CreatedAtUtc.AddHours(1));

        Assert.True(
            AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
                conversation, CreatedAtUtc.AddHours(26)));
    }

    [Fact]
    public void IsPurgeTargetReached_NormalizesNonUtcClock()
    {
        var conversation = DeletedAt(CreatedAtUtc.AddHours(1));
        var dueInSriLanka = CreatedAtUtc.AddHours(25).ToOffset(
            TimeSpan.FromMinutes(330));

        Assert.False(
            AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
                conversation, dueInSriLanka.AddTicks(-1)));
        Assert.True(
            AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
                conversation, dueInSriLanka));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void NullConversation_ThrowsArgumentNullException(int operation)
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
        {
            switch (operation)
            {
                case 0:
                    AiConversationRetentionPolicy.Classify(null!, CreatedAtUtc);
                    break;
                case 1:
                    AiConversationRetentionPolicy.MustHideFromHistory(
                        null!, CreatedAtUtc);
                    break;
                case 2:
                    AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(
                        null!, CreatedAtUtc);
                    break;
                case 3:
                    AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(
                        null!);
                    break;
                default:
                    AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
                        null!, CreatedAtUtc);
                    break;
            }
        });

        Assert.Equal("conversation", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DefaultServerNow_IsRejected(int operation)
    {
        var conversation = Create();
        var error = Assert.Throws<ArgumentException>(() =>
        {
            switch (operation)
            {
                case 0:
                    AiConversationRetentionPolicy.Classify(conversation, default);
                    break;
                case 1:
                    AiConversationRetentionPolicy.MustHideFromHistory(
                        conversation, default);
                    break;
                case 2:
                    AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(
                        conversation, default);
                    break;
                default:
                    AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
                        conversation, default);
                    break;
            }
        });

        Assert.Equal("serverNowUtc", error.ParamName);
    }

    [Fact]
    public void DefaultServerNow_IsRejectedEvenWhenDeleted()
    {
        var conversation = DeletedAt(CreatedAtUtc.AddHours(1));

        var error = Assert.Throws<ArgumentException>(() =>
            AiConversationRetentionPolicy.Classify(conversation, default));
        Assert.Equal("serverNowUtc", error.ParamName);
    }

    // These values cannot be created through the public entity factory.
    // Reflection deliberately simulates malformed ORM/materialized rows.
    [Theory]
    [InlineData("EmptyId")]
    [InlineData("EmptyAccountId")]
    [InlineData("EmptyProfileId")]
    [InlineData("EmptyCreateOperationId")]
    [InlineData("InvalidModuleZero")]
    [InlineData("InvalidModuleUnknown")]
    [InlineData("DefaultCreatedAt")]
    [InlineData("NonUtcCreatedAt")]
    [InlineData("LastSavedBeforeCreation")]
    [InlineData("NonUtcLastSavedAt")]
    [InlineData("IncorrectExpiry")]
    [InlineData("NonUtcExpiry")]
    [InlineData("DeletedBeforeCreation")]
    [InlineData("NonUtcDeletedAt")]
    public void MalformedPersistedConversation_FailsClosed(string defect)
    {
        var conversation = Create();
        CorruptConversation(conversation, defect);

        AssertInvalidState(() =>
            AiConversationRetentionPolicy.Classify(
                conversation, CreatedAtUtc.AddDays(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryEntryPoint_RejectsMalformedConversation(int operation)
    {
        var conversation = Create();
        SetPrivateProperty(conversation, "AccountId", Guid.Empty);

        AssertInvalidState(() =>
        {
            switch (operation)
            {
                case 0:
                    AiConversationRetentionPolicy.Classify(
                        conversation, CreatedAtUtc);
                    break;
                case 1:
                    AiConversationRetentionPolicy.MustHideFromHistory(
                        conversation, CreatedAtUtc);
                    break;
                case 2:
                    AiConversationRetentionPolicy.RequiresActivePurgeEvaluation(
                        conversation, CreatedAtUtc);
                    break;
                case 3:
                    AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(
                        conversation);
                    break;
                default:
                    AiConversationRetentionPolicy.IsManualDeletionPurgeTargetReached(
                        conversation, CreatedAtUtc);
                    break;
            }
        });
    }

    private static AiConversationEntity Create(
        AiModule module = AiModule.Learning)
    {
        var actor = new AiAuthorizedActor(
            Guid.NewGuid(), Guid.NewGuid(), module);

        return AiConversationEntity.CreateForFirstUserMessage(
            actor, Guid.NewGuid(), CreatedAtUtc);
    }

    private static AiConversationEntity DeletedAt(DateTimeOffset deletedAt)
    {
        var conversation = Create();
        conversation.MarkDeleted(deletedAt);
        return conversation;
    }

    private static void AssertInvalidState(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("Invalid AI conversation retention state.", error.Message);
    }

    private static void SetPrivateProperty(
        AiConversationEntity conversation, string propertyName, object? value)
    {
        var property = typeof(AiConversationEntity).GetProperty(
            propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        var setter = property!.GetSetMethod(nonPublic: true);
        Assert.NotNull(setter);
        setter!.Invoke(conversation, new object?[] { value });
    }

    private static void CorruptConversation(
        AiConversationEntity conversation, string defect)
    {
        switch (defect)
        {
            case "EmptyId":
                SetPrivateProperty(conversation, "Id", Guid.Empty);
                break;
            case "EmptyAccountId":
                SetPrivateProperty(conversation, "AccountId", Guid.Empty);
                break;
            case "EmptyProfileId":
                SetPrivateProperty(conversation, "ModuleProfileId", Guid.Empty);
                break;
            case "EmptyCreateOperationId":
                SetPrivateProperty(conversation, "CreateOperationId", Guid.Empty);
                break;
            case "InvalidModuleZero":
                SetPrivateProperty(conversation, "Module", (AiModule)0);
                break;
            case "InvalidModuleUnknown":
                SetPrivateProperty(conversation, "Module", (AiModule)99);
                break;
            case "DefaultCreatedAt":
                SetPrivateProperty(conversation, "CreatedAtUtc", default(DateTimeOffset));
                break;
            case "NonUtcCreatedAt":
                SetPrivateProperty(conversation, "CreatedAtUtc",
                    CreatedAtUtc.ToOffset(TimeSpan.FromMinutes(330)));
                break;
            case "LastSavedBeforeCreation":
                SetPrivateProperty(conversation, "LastSavedUserMessageAtUtc",
                    CreatedAtUtc.AddTicks(-1));
                break;
            case "NonUtcLastSavedAt":
                SetPrivateProperty(conversation, "LastSavedUserMessageAtUtc",
                    CreatedAtUtc.ToOffset(TimeSpan.FromMinutes(330)));
                break;
            case "IncorrectExpiry":
                SetPrivateProperty(conversation, "ExpiresAtUtc",
                    ExpiresAtUtc.AddTicks(1));
                break;
            case "NonUtcExpiry":
                SetPrivateProperty(conversation, "ExpiresAtUtc",
                    ExpiresAtUtc.ToOffset(TimeSpan.FromMinutes(330)));
                break;
            case "DeletedBeforeCreation":
                SetPrivateProperty(conversation, "DeletedAtUtc",
                    CreatedAtUtc.AddTicks(-1));
                break;
            case "NonUtcDeletedAt":
                SetPrivateProperty(conversation, "DeletedAtUtc",
                    CreatedAtUtc.AddHours(1).ToOffset(
                        TimeSpan.FromMinutes(330)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }
    }
}
