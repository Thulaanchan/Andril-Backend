using System.Text;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

/// <summary>Domain characterization only; no PostgreSQL execution or storage approval.</summary>
public sealed class ChatStorageCompatibilityTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IEnumerable<object[]> PreservedText()
    {
        string[] texts = ["Hello, world!", "வணக்கம் தமிழ்", "😀 👩🏽‍💻 ❤️", "first\nsecond", "first\r\nsecond", " \t content \n ", "e\u0301 and é"];
        foreach (var module in Enum.GetValues<ChatModule>())
        {
            foreach (var text in texts)
            {
                yield return [module, text];
            }
        }
    }

    [Theory]
    [MemberData(nameof(PreservedText))]
    public void Domain_preserves_text_and_strict_utf8_round_trip(ChatModule module, string text)
    {
        var message = Create(module, text, DateTimeOffset.UtcNow);
        var draft = ChatTextMessageDraft.Create(module, message.ConversationId, text);
        Assert.Equal(text, message.Text);
        Assert.Equal(text, draft.Text);
        Assert.Equal(text, StrictUtf8.GetString(StrictUtf8.GetBytes(text)));
        Assert.Null(message.MoodTone);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void Domain_currently_preserves_embedded_nul(ChatModule module)
    {
        // PostgreSQL text cannot store NUL. This observes the domain mismatch,
        // without executing a database command or approving silent sanitization.
        const string text = "prefix\0suffix";
        var message = Create(module, text, DateTimeOffset.UtcNow);
        Assert.Equal(text, message.Text);
        Assert.Equal(text, ChatTextMessageDraft.Create(module, message.ConversationId, text).Text);
        Assert.Contains('\0', message.Text);
        Assert.Contains((byte)0, StrictUtf8.GetBytes(message.Text));
    }

    [Theory]
    [InlineData(ChatModule.Learning, "high")]
    [InlineData(ChatModule.Learning, "low")]
    [InlineData(ChatModule.Learning, "broken")]
    [InlineData(ChatModule.Hobbies, "high")]
    [InlineData(ChatModule.Hobbies, "low")]
    [InlineData(ChatModule.Hobbies, "broken")]
    [InlineData(ChatModule.Social, "high")]
    [InlineData(ChatModule.Social, "low")]
    [InlineData(ChatModule.Social, "broken")]
    public void Domain_preserves_malformed_utf16_that_strict_utf8_cannot_encode(ChatModule module, string scenario)
    {
        var text = scenario switch
        {
            "high" => "prefix\ud800suffix",
            "low" => "prefix\udc00suffix",
            _ => "\ud800A\udc00"
        };
        var message = Create(module, text, DateTimeOffset.UtcNow);
        Assert.Equal(text, message.Text);
        Assert.Equal(text, ChatTextMessageDraft.Create(module, message.ConversationId, text).Text);
        Assert.Throws<EncoderFallbackException>(() => StrictUtf8.GetBytes(message.Text));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(330)]
    [InlineData(-420)]
    [InlineData(840)]
    [InlineData(-720)]
    public void Domain_normalizes_offset_to_utc_without_changing_instant(int offsetMinutes)
    {
        var timestamp = new DateTimeOffset(2030, 3, 4, 12, 0, 0, TimeSpan.FromMinutes(offsetMinutes));
        var message = Create(ChatModule.Learning, "timestamp", timestamp);
        Assert.Equal(TimeSpan.Zero, message.CreatedAtUtc.Offset);
        Assert.Equal(timestamp.UtcDateTime, message.CreatedAtUtc.UtcDateTime);
    }

    [Fact]
    public void Domain_retains_sub_microsecond_ticks_requiring_a_storage_policy()
    {
        var timestamp = new DateTimeOffset(2030, 3, 4, 12, 0, 0, TimeSpan.Zero).AddTicks(7);
        var message = Create(ChatModule.Learning, "precision", timestamp);
        Assert.Equal(timestamp, message.CreatedAtUtc);
        Assert.Equal(7, message.CreatedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond);
        // Illustrative microsecond projection only, not an observed provider
        // round trip or an approved timestamp truncation policy.
        var projectedTicks = timestamp.Ticks / TimeSpan.TicksPerMicrosecond * TimeSpan.TicksPerMicrosecond;
        Assert.NotEqual(timestamp.Ticks, projectedTicks);
    }

    private static ChatMessage Create(ChatModule module, string text, DateTimeOffset timestamp)
    {
        var sender = ChatParticipantReference.Create(module, Guid.NewGuid(), Guid.NewGuid());
        var conversation = ChatConversation.Create(module, Guid.NewGuid(), [sender]);
        return ChatMessage.CreateFromServerValues(module, Guid.NewGuid(), conversation.ConversationId,
            conversation, sender, text, timestamp);
    }
}
