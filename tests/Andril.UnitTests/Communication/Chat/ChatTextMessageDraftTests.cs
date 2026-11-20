using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatTextMessageDraftTests
{
    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void SupportedModuleAndConversation_ShouldPreserveTarget(ChatModule module)
    {
        var conversationId = Guid.NewGuid();

        var draft = ChatTextMessageDraft.Create(module, conversationId, "Hello");

        Assert.Equal(module, draft.Module);
        Assert.Equal(conversationId, draft.ConversationId);
        Assert.Null(draft.MoodTone);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void UndefinedModule_ShouldReject(int module)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChatTextMessageDraft.Create((ChatModule)module, Guid.NewGuid(), "Hello"));

        Assert.Equal("module", error.ParamName);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void EmptyConversation_ShouldRejectWithoutReplacement(ChatModule module)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ChatTextMessageDraft.Create(module, Guid.Empty, "Hello"));

        Assert.Equal("conversationId", error.ParamName);
    }

    [Fact]
    public void NullText_ShouldReject()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            ChatTextMessageDraft.Create(ChatModule.Learning, Guid.NewGuid(), null));

        Assert.Equal("text", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData(" \t\n ")]
    [InlineData("\u00a0\u2003\u3000")]
    public void EmptyOrWhitespaceText_ShouldReject(string text)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ChatTextMessageDraft.Create(ChatModule.Hobbies, Guid.NewGuid(), text));

        Assert.Equal("text", error.ParamName);
    }

    public static IEnumerable<object[]> ValidTextByModule()
    {
        string[] messages =
        [
            "Hello, world!",
            "வணக்கம் தமிழ்",
            "😀 👩🏽‍💻 ❤️",
            "First line\nSecond line",
            "First line\r\nSecond line",
            " \t வணக்கம் 😀 \n ",
            "e\u0301 and é",
            new string('x', 100_000)
        ];

        foreach (var module in Enum.GetValues<ChatModule>())
        {
            foreach (var text in messages)
            {
                yield return [module, text];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ValidTextByModule))]
    public void LegitimateText_ShouldRemainExactlyUnchanged(ChatModule module, string text)
    {
        var draft = ChatTextMessageDraft.Create(module, Guid.NewGuid(), text);

        Assert.Equal(text, draft.Text);
    }

    public static IEnumerable<object[]> NonNullMoodTonesByModule()
    {
        string[] tones = ["", " ", "\t\n", "unapproved-tone", "Happy", "மகிழ்ச்சி"];

        foreach (var module in Enum.GetValues<ChatModule>())
        {
            foreach (var tone in tones)
            {
                yield return [module, tone];
            }
        }
    }

    [Theory]
    [MemberData(nameof(NonNullMoodTonesByModule))]
    public void AnyNonNullMoodTone_ShouldRejectWithoutDiscardingIt(ChatModule module, string moodTone)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ChatTextMessageDraft.Create(module, Guid.NewGuid(), "Hello", moodTone));

        Assert.Equal("moodTone", error.ParamName);
        Assert.DoesNotContain("Hello", error.Message);
    }

    [Fact]
    public void SameConversationAcrossModules_ShouldKeepDistinctModuleTargets()
    {
        var conversationId = Guid.NewGuid();
        var drafts = Enum.GetValues<ChatModule>()
            .Select(module => ChatTextMessageDraft.Create(module, conversationId, "Hello"))
            .ToArray();

        Assert.Equal(3, drafts.Select(draft => draft.Module).Distinct().Count());
        Assert.All(drafts, draft => Assert.Equal(conversationId, draft.ConversationId));
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task ValidOrInvalidContent_ShouldNeverGrantAccess(ChatModule module)
    {
        IChatAccessAuthorizer authorizer = new DenyAllChatAccessAuthorizer();
        var conversationId = Guid.NewGuid();

        var draft = ChatTextMessageDraft.Create(module, conversationId, "Hello");
        Assert.Null(await authorizer.AuthorizeConversationAsync(draft.Module, draft.ConversationId));

        Assert.Throws<ArgumentException>(() =>
            ChatTextMessageDraft.Create(module, conversationId, " "));
        Assert.Null(await authorizer.AuthorizeConversationAsync(module, conversationId));

        Assert.Throws<ArgumentException>(() =>
            ChatTextMessageDraft.Create(module, conversationId, "Hello", "unapproved-tone"));
        Assert.Null(await authorizer.AuthorizeConversationAsync(module, conversationId));
    }
}
