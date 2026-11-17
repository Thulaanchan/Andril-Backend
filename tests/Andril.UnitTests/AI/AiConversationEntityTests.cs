
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// AI-BE-03 Step 9: synthetic-data tests for the proposed conversation entity.
/// These tests do not prove database authorization, transactional persistence,
/// cleanup jobs, or HTTP security.
/// </summary>
public sealed class AiConversationEntityTests
{
    private static readonly DateTimeOffset SavedAtUtc =
        new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpiresAtUtc =
        SavedAtUtc.AddDays(14);

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void Create_PreservesExactModuleActorAndDeadline(
        AiModule module)
    {
        var actor = Actor(module);
        var operationId = Guid.NewGuid();

        var entity = AiConversationEntity.CreateForFirstUserMessage(
            actor, operationId, SavedAtUtc);

        Assert.NotEqual(Guid.Empty, entity.Id);
        Assert.Equal(actor.UserId, entity.AccountId);
        Assert.Equal(actor.ModuleProfileId, entity.ModuleProfileId);
        Assert.Equal(module, entity.Module);
        Assert.Equal(operationId, entity.CreateOperationId);
        Assert.Equal(SavedAtUtc, entity.CreatedAtUtc);
        Assert.Equal(SavedAtUtc, entity.LastSavedUserMessageAtUtc);
        Assert.Equal(ExpiresAtUtc, entity.ExpiresAtUtc);
        Assert.Null(entity.DeletedAtUtc);
    }

    [Fact]
    public void Create_NormalizesServerTimeToUtc()
    {
        var sriLankaTime = new DateTimeOffset(
            2026, 11, 16, 15, 30, 0,
            TimeSpan.FromMinutes(330));

        var entity = Create(Actor(), sriLankaTime);

        Assert.Equal(SavedAtUtc, entity.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, entity.CreatedAtUtc.Offset);
        Assert.Equal(SavedAtUtc, entity.LastSavedUserMessageAtUtc);
        Assert.Equal(ExpiresAtUtc, entity.ExpiresAtUtc);
    }

    [Fact]
    public void Create_TwoSeparateEntitiesReceiveDifferentIdentifiers()
    {
        var actor = Actor();
        var first = Create(actor);
        var second = Create(actor);

        Assert.NotEqual(first.Id, second.Id);

        // Operation-level idempotency belongs to the repository.
    }

    [Fact]
    public void Create_NullActorThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Create(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Create_InvalidActorThrows(int caseNumber)
    {
        var actor = Actor();

        actor = caseNumber switch
        {
            0 => actor with { UserId = Guid.Empty },
            1 => actor with { ModuleProfileId = Guid.Empty },
            2 => actor with { Module = (AiModule)0 },
            _ => actor with { Module = (AiModule)99 }
        };

        var exception =
            Assert.Throws<ArgumentException>(() => Create(actor));

        Assert.Equal("actor", exception.ParamName);
    }

    [Fact]
    public void Create_EmptyOperationIdThrows()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AiConversationEntity.CreateForFirstUserMessage(
                Actor(), Guid.Empty, SavedAtUtc));

        Assert.Equal("createOperationId", exception.ParamName);
    }

    [Fact]
    public void Create_DefaultServerTimestampThrows()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AiConversationEntity.CreateForFirstUserMessage(
                Actor(), Guid.NewGuid(), default));

        Assert.Equal("serverSavedAtUtc", exception.ParamName);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void Access_ExactActorBeforeExpiryIsAllowed(
        AiModule module)
    {
        var actor = Actor(module);
        var entity = Create(actor);

        Assert.True(entity.IsAccessibleBy(
            actor, SavedAtUtc.AddDays(1)));
    }

    [Fact]
    public void Access_NullActorIsDenied()
    {
        var entity = Create(Actor());

        Assert.False(entity.IsAccessibleBy(
            null, SavedAtUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Access_InvalidActorIsDenied(int caseNumber)
    {
        var owner = Actor();
        var entity = Create(owner);

        var invalid = caseNumber switch
        {
            0 => owner with { UserId = Guid.Empty },
            1 => owner with { ModuleProfileId = Guid.Empty },
            _ => owner with { Module = (AiModule)99 }
        };

        Assert.False(entity.IsAccessibleBy(
            invalid, SavedAtUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Access_OtherIdentityCannotReadConversation(
        int caseNumber)
    {
        var owner = Actor(AiModule.Learning);
        var entity = Create(owner);

        var other = caseNumber switch
        {
            0 => owner with { UserId = Guid.NewGuid() },
            1 => owner with { ModuleProfileId = Guid.NewGuid() },
            _ => owner with { Module = AiModule.Hobbies }
        };

        Assert.False(entity.IsAccessibleBy(
            other, SavedAtUtc.AddHours(1)));
    }

    [Fact]
    public void Access_OneTickBeforeExpiryIsAllowed()
    {
        var actor = Actor();
        var entity = Create(actor);

        Assert.True(entity.IsAccessibleBy(
            actor, ExpiresAtUtc.AddTicks(-1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Access_AtOrAfterExpiryIsDenied(int secondsAfter)
    {
        var actor = Actor();
        var entity = Create(actor);

        Assert.False(entity.IsAccessibleBy(
            actor,
            ExpiresAtUtc.AddSeconds(secondsAfter)));
    }

    [Fact]
    public void Access_NormalizesCurrentTimeOffset()
    {
        var actor = Actor();
        var entity = Create(actor);

        var localExpiry = new DateTimeOffset(
            2026, 11, 30, 15, 30, 0,
            TimeSpan.FromMinutes(330));

        Assert.False(entity.IsAccessibleBy(
            actor, localExpiry));

        Assert.True(entity.IsAccessibleBy(
            actor, localExpiry.AddTicks(-1)));
    }

    [Fact]
    public void Access_DefaultCurrentTimestampForOwnerThrows()
    {
        var actor = Actor();
        var entity = Create(actor);

        var exception = Assert.Throws<ArgumentException>(() =>
            entity.IsAccessibleBy(actor, default));

        Assert.Equal("nowUtc", exception.ParamName);
    }

    [Fact]
    public void SaveUserMessage_RenewsFourteenDayExpiryAndPreservesCreation()
    {
        var entity = Create(Actor());
        var savedAgain = SavedAtUtc.AddDays(5);

        entity.ApplySavedUserMessage(savedAgain);

        Assert.Equal(SavedAtUtc, entity.CreatedAtUtc);
        Assert.Equal(
            savedAgain,
            entity.LastSavedUserMessageAtUtc);
        Assert.Equal(
            savedAgain.AddDays(14),
            entity.ExpiresAtUtc);
    }

    [Fact]
    public void SaveUserMessage_NormalizesOffsetToUtc()
    {
        var entity = Create(Actor());

        var sriLankaTime = new DateTimeOffset(
            2026, 11, 21, 15, 30, 0,
            TimeSpan.FromMinutes(330));

        entity.ApplySavedUserMessage(sriLankaTime);

        Assert.Equal(
            SavedAtUtc.AddDays(5),
            entity.LastSavedUserMessageAtUtc);

        Assert.Equal(
            ExpiresAtUtc.AddDays(5),
            entity.ExpiresAtUtc);

        Assert.Equal(
            TimeSpan.Zero,
            entity.ExpiresAtUtc.Offset);
    }

    [Fact]
    public void SaveUserMessage_SameTimestampDoesNotExtendExpiry()
    {
        var entity = Create(Actor());

        entity.ApplySavedUserMessage(SavedAtUtc);

        Assert.Equal(
            SavedAtUtc,
            entity.LastSavedUserMessageAtUtc);

        Assert.Equal(
            ExpiresAtUtc,
            entity.ExpiresAtUtc);
    }

    [Fact]
    public void SaveUserMessage_EarlierTimestampIsRejectedWithoutMutation()
    {
        var entity = Create(Actor());

        Assert.Throws<InvalidOperationException>(() =>
            entity.ApplySavedUserMessage(
                SavedAtUtc.AddTicks(-1)));

        Assert.Equal(
            SavedAtUtc,
            entity.LastSavedUserMessageAtUtc);

        Assert.Equal(
            ExpiresAtUtc,
            entity.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SaveUserMessage_AtOrAfterExpiryIsDenied(
        int secondsAfter)
    {
        var entity = Create(Actor());

        Assert.Throws<InvalidOperationException>(() =>
            entity.ApplySavedUserMessage(
                ExpiresAtUtc.AddSeconds(secondsAfter)));

        Assert.Equal(
            SavedAtUtc,
            entity.LastSavedUserMessageAtUtc);

        Assert.Equal(
            ExpiresAtUtc,
            entity.ExpiresAtUtc);
    }

    [Fact]
    public void SaveUserMessage_DefaultTimestampThrows()
    {
        var entity = Create(Actor());

        var exception = Assert.Throws<ArgumentException>(() =>
            entity.ApplySavedUserMessage(default));

        Assert.Equal(
            "serverSavedAtUtc",
            exception.ParamName);
    }

    [Fact]
    public void SaveUserMessage_AfterDeletionIsDeniedWithoutMutation()
    {
        var entity = Create(Actor());

        entity.MarkDeleted(
            SavedAtUtc.AddHours(1));

        Assert.Throws<InvalidOperationException>(() =>
            entity.ApplySavedUserMessage(
                SavedAtUtc.AddHours(2)));

        Assert.Equal(
            SavedAtUtc,
            entity.LastSavedUserMessageAtUtc);

        Assert.Equal(
            ExpiresAtUtc,
            entity.ExpiresAtUtc);
    }

    [Fact]
    public void Deletion_ImmediatelyBlocksAccessAndStoresUtcTimestamp()
    {
        var actor = Actor();
        var entity = Create(actor);

        var localDeletedAt = new DateTimeOffset(
            2026, 11, 16, 16, 30, 0,
            TimeSpan.FromMinutes(330));

        entity.MarkDeleted(localDeletedAt);

        Assert.Equal(
            SavedAtUtc.AddHours(1),
            entity.DeletedAtUtc);

        Assert.Equal(
            TimeSpan.Zero,
            entity.DeletedAtUtc!.Value.Offset);

        Assert.False(entity.IsAccessibleBy(
            actor, SavedAtUtc.AddHours(2)));
    }

    [Fact]
    public void Deletion_IsIdempotentAndPreservesOriginalTimestamp()
    {
        var entity = Create(Actor());
        var firstDeletion = SavedAtUtc.AddHours(1);

        entity.MarkDeleted(firstDeletion);
        entity.MarkDeleted(firstDeletion.AddHours(1));

        Assert.Equal(
            firstDeletion,
            entity.DeletedAtUtc);
    }

    [Fact]
    public void Deletion_BeforeCreationIsRejectedWithoutMutation()
    {
        var entity = Create(Actor());

        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                entity.MarkDeleted(
                    SavedAtUtc.AddTicks(-1)));

        Assert.Equal(
            "serverDeletedAtUtc",
            exception.ParamName);

        Assert.Null(entity.DeletedAtUtc);
    }

    [Fact]
    public void Deletion_DefaultTimestampThrows()
    {
        var entity = Create(Actor());

        var exception = Assert.Throws<ArgumentException>(() =>
            entity.MarkDeleted(default));

        Assert.Equal(
            "serverDeletedAtUtc",
            exception.ParamName);

        Assert.Null(entity.DeletedAtUtc);
    }

    [Fact]
    public void Deletion_AtCreationInstantIsPermitted()
    {
        var entity = Create(Actor());

        entity.MarkDeleted(SavedAtUtc);

        Assert.Equal(
            SavedAtUtc,
            entity.DeletedAtUtc);
    }

    [Fact]
    public void Access_ReadChecksDoNotChangeExpiryOrLastSavedUserMessage()
    {
        var actor = Actor();
        var entity = Create(actor);

        Assert.True(entity.IsAccessibleBy(
            actor, SavedAtUtc.AddDays(1)));

        Assert.True(entity.IsAccessibleBy(
            actor, SavedAtUtc.AddDays(2)));

        Assert.Equal(
            SavedAtUtc,
            entity.LastSavedUserMessageAtUtc);

        Assert.Equal(
            ExpiresAtUtc,
            entity.ExpiresAtUtc);
    }

    private static AiAuthorizedActor Actor(
        AiModule module = AiModule.Learning) =>
        new(Guid.NewGuid(), Guid.NewGuid(), module);

    private static AiConversationEntity Create(
        AiAuthorizedActor actor,
        DateTimeOffset? savedAt = null) =>
        AiConversationEntity.CreateForFirstUserMessage(
            actor,
            Guid.NewGuid(),
            savedAt ?? SavedAtUtc);
}
