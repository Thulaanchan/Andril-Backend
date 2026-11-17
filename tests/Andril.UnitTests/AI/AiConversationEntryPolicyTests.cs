using System;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// AI-BE-03 Step 17: pure entry-routing tests using synthetic actors.
/// No authentication, database, clock service or HTTP endpoint is exercised.
/// </summary>
public sealed class AiConversationEntryPolicyTests
{
    private static readonly DateTimeOffset Created =
        new(2026, 11, 16, 17, 0, 0, TimeSpan.Zero);

    // Local midnight in Sri Lanka on November 17.
    private static readonly DateTimeOffset Midnight = Created.AddHours(1.5);
    private static readonly DateTimeOffset Before = Midnight.AddMinutes(-1);
    private static readonly DateTimeOffset After = Midnight.AddMinutes(1);

    // Fixed offset avoids relying on Windows/Linux time-zone IDs.
    private static readonly TimeZoneInfo ProfileZone =
        TimeZoneInfo.CreateCustomTimeZone(
            "Test-SriLanka", TimeSpan.FromMinutes(330),
            "Test Sri Lanka", "Test Sri Lanka");

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void FirstIntentionalEntry_StartsFresh(AiModule module)
    {
        var actor = Actor(module);
        Expect(AiConversationEntryAction.StartFresh,
            AiConversationEntryPolicy.Decide(actor,
                AiConversationEntryIntent.IntentionalEntry,
                null, After, ProfileZone));
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void NewLocalDay_StartsFreshWithoutOldConversation(AiModule module)
    {
        var actor = Actor(module);
        var conversation = Conversation(actor);

        Expect(AiConversationEntryAction.StartFresh,
            Entry(actor, conversation, Midnight, Before));
    }

    [Fact]
    public void SameLocalDay_WithSelectedChat_Continues()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        Expect(AiConversationEntryAction.ContinueSelected,
            Entry(actor, conversation, Before, Created), conversation.Id);
    }

    [Fact]
    public void SameLocalDay_WithoutSelectedChat_StartsFresh()
    {
        Expect(AiConversationEntryAction.StartFresh,
            Entry(Actor(), null, Before, Created));
    }

    [Fact]
    public void UtcMidnight_WithinSameLocalDay_DoesNotForceFreshChat()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var previous = new DateTimeOffset(2026, 11, 16, 23, 55, 0, TimeSpan.Zero);
        var now = previous.AddMinutes(10);

        Expect(AiConversationEntryAction.ContinueSelected,
            Entry(actor, conversation, now, previous), conversation.Id);
    }

    [Fact]
    public void LocalClockOffset_IsNormalizedBeforeDayComparison()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        Expect(AiConversationEntryAction.StartFresh,
            Entry(actor, conversation,
                Midnight.ToOffset(TimeSpan.FromMinutes(330)), Before));
    }

    [Theory]
    [InlineData(AiConversationEntryIntent.ContinueOpenChat)]
    [InlineData(AiConversationEntryIntent.ExplicitResume)]
    public void ContinueOrResume_AcrossLocalMidnight_KeepsSelectedChat(
        AiConversationEntryIntent intent)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        Expect(AiConversationEntryAction.ContinueSelected,
            AiConversationEntryPolicy.Decide(actor, intent, conversation,
                After, ProfileZone, Before), conversation.Id);
    }

    [Fact]
    public void ExplicitResume_ValidOlderChat_DoesNotRequireEntryTimeZone()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        Expect(AiConversationEntryAction.ContinueSelected,
            AiConversationEntryPolicy.Decide(actor,
                AiConversationEntryIntent.ExplicitResume, conversation,
                Created.AddDays(7)), conversation.Id);
    }

    [Fact]
    public void NewChat_DoesNotReturnPriorConversationId()
    {
        var actor = Actor();
        Expect(AiConversationEntryAction.StartFresh,
            AiConversationEntryPolicy.Decide(actor,
                AiConversationEntryIntent.NewChat, Conversation(actor), After));
    }

    [Theory]
    [InlineData(AiConversationEntryIntent.NewChat)]
    [InlineData(AiConversationEntryIntent.IntentionalEntry)]
    public void FreshChat_DoesNotReturnForeignConversationId(
        AiConversationEntryIntent intent)
    {
        var actor = Actor();
        var foreign = Conversation(Actor(AiModule.Hobbies));
        Expect(AiConversationEntryAction.StartFresh,
            AiConversationEntryPolicy.Decide(actor, intent, foreign,
                After, ProfileZone, Before));
    }

    [Theory]
    [InlineData(AiConversationEntryIntent.IntentionalEntry)]
    [InlineData(AiConversationEntryIntent.ContinueOpenChat)]
    [InlineData(AiConversationEntryIntent.ExplicitResume)]
    [InlineData(AiConversationEntryIntent.NewChat)]
    public void MissingActor_AlwaysDenied(AiConversationEntryIntent intent)
    {
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(null, intent, null,
                After, ProfileZone, Before));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidActor_EvenNewChatIsDenied(int defect)
    {
        var actor = Actor();
        actor = defect switch
        {
            0 => actor with { UserId = Guid.Empty },
            1 => actor with { ModuleProfileId = Guid.Empty },
            2 => actor with { Module = (AiModule)0 },
            _ => actor with { Module = (AiModule)99 }
        };
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(actor,
                AiConversationEntryIntent.NewChat, null, After));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void UnknownIntent_IsDenied(int intent)
    {
        var actor = Actor();
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(actor,
                (AiConversationEntryIntent)intent, Conversation(actor), After));
    }

    [Fact]
    public void DefaultServerTime_IsDenied()
    {
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(Actor(),
                AiConversationEntryIntent.NewChat, null, default));
    }

    [Fact]
    public void IntentionalEntry_WithoutTrustedTimeZone_IsDenied()
    {
        var actor = Actor();
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(actor,
                AiConversationEntryIntent.IntentionalEntry,
                Conversation(actor), After));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntentionalEntry_InvalidPreviousEntry_IsDenied(bool future)
    {
        var actor = Actor();
        var previous = future ? After.AddTicks(1) : default(DateTimeOffset);
        Expect(AiConversationEntryAction.Denied,
            Entry(actor, Conversation(actor), After, previous));
    }

    [Theory]
    [InlineData(AiConversationEntryIntent.ContinueOpenChat)]
    [InlineData(AiConversationEntryIntent.ExplicitResume)]
    public void ContinueOrResume_RequiresSelectedConversation(
        AiConversationEntryIntent intent)
    {
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(Actor(), intent, null, After));
    }

    [Theory]
    [InlineData(AiConversationEntryIntent.ContinueOpenChat, 0)]
    [InlineData(AiConversationEntryIntent.ContinueOpenChat, 1)]
    [InlineData(AiConversationEntryIntent.ContinueOpenChat, 2)]
    [InlineData(AiConversationEntryIntent.ExplicitResume, 0)]
    [InlineData(AiConversationEntryIntent.ExplicitResume, 1)]
    [InlineData(AiConversationEntryIntent.ExplicitResume, 2)]
    [InlineData(AiConversationEntryIntent.IntentionalEntry, 0)]
    [InlineData(AiConversationEntryIntent.IntentionalEntry, 1)]
    [InlineData(AiConversationEntryIntent.IntentionalEntry, 2)]
    public void SelectedChat_OtherAccountProfileOrModule_IsDenied(
        AiConversationEntryIntent intent, int mismatch)
    {
        var owner = Actor();
        var conversation = Conversation(owner);
        var caller = mismatch switch
        {
            0 => owner with { UserId = Guid.NewGuid() },
            1 => owner with { ModuleProfileId = Guid.NewGuid() },
            _ => owner with { Module = AiModule.Hobbies }
        };
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(caller, intent, conversation,
                Before, ProfileZone, Created));
    }

    [Theory]
    [InlineData(AiConversationEntryIntent.ContinueOpenChat)]
    [InlineData(AiConversationEntryIntent.ExplicitResume)]
    public void SelectedChat_OneTickBeforeExpiryAllowed_AtExpiryDenied(
        AiConversationEntryIntent intent)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var expiry = Created.AddDays(14);
        Expect(AiConversationEntryAction.ContinueSelected,
            AiConversationEntryPolicy.Decide(actor, intent, conversation,
                expiry.AddTicks(-1)), conversation.Id);
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(actor, intent, conversation, expiry));
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(actor, intent, conversation,
                expiry.AddTicks(1)));
    }

    [Theory]
    [InlineData(AiConversationEntryIntent.IntentionalEntry)]
    [InlineData(AiConversationEntryIntent.ContinueOpenChat)]
    [InlineData(AiConversationEntryIntent.ExplicitResume)]
    public void SelectedChat_Deleted_IsDenied(AiConversationEntryIntent intent)
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        conversation.MarkDeleted(Created.AddMinutes(1));
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(actor, intent, conversation,
                Before, ProfileZone, Created));
    }

    [Fact]
    public void SelectedChat_FutureSavedUserTimestamp_IsDenied()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        conversation.ApplySavedUserMessage(After.AddMinutes(1));
        Expect(AiConversationEntryAction.Denied,
            AiConversationEntryPolicy.Decide(actor,
                AiConversationEntryIntent.ExplicitResume, conversation, After));
    }

    [Fact]
    public void Resume_AfterSuccessfulUserSave_UsesRenewedExpiry()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        conversation.ApplySavedUserMessage(Created.AddDays(5));
        Expect(AiConversationEntryAction.ContinueSelected,
            AiConversationEntryPolicy.Decide(actor,
                AiConversationEntryIntent.ExplicitResume,
                conversation, Created.AddDays(14)), conversation.Id);
    }

    [Fact]
    public void EntryAndResume_DoNotRenewExpiryOrChangeConversation()
    {
        var actor = Actor();
        var conversation = Conversation(actor);
        var id = conversation.Id;
        var expiry = conversation.ExpiresAtUtc;
        var last = conversation.LastSavedUserMessageAtUtc;
        Expect(AiConversationEntryAction.ContinueSelected,
            Entry(actor, conversation, Before, Created), id);
        Expect(AiConversationEntryAction.StartFresh,
            Entry(actor, conversation, After, Before));
        Expect(AiConversationEntryAction.ContinueSelected,
            AiConversationEntryPolicy.Decide(actor,
                AiConversationEntryIntent.ExplicitResume, conversation, After), id);
        Assert.Equal(id, conversation.Id);
        Assert.Equal(expiry, conversation.ExpiresAtUtc);
        Assert.Equal(last, conversation.LastSavedUserMessageAtUtc);
        Assert.Null(conversation.DeletedAtUtc);
    }

    private static AiAuthorizedActor Actor(AiModule module = AiModule.Learning) =>
        new(Guid.NewGuid(), Guid.NewGuid(), module);

    private static AiConversationEntity Conversation(AiAuthorizedActor actor) =>
        AiConversationEntity.CreateForFirstUserMessage(actor, Guid.NewGuid(), Created);

    private static AiConversationEntryDecision Entry(
        AiAuthorizedActor actor, AiConversationEntity? selected,
        DateTimeOffset now, DateTimeOffset previous) =>
        AiConversationEntryPolicy.Decide(actor,
            AiConversationEntryIntent.IntentionalEntry,
            selected, now, ProfileZone, previous);

    private static void Expect(AiConversationEntryAction action,
        AiConversationEntryDecision actual, Guid? conversationId = null)
    {
        Assert.Equal(action, actual.Action);
        Assert.Equal(conversationId, actual.ConversationId);
    }
}
