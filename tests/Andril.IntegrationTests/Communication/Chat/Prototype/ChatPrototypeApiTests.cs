using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat.Prototype;

[Trait("Step", "08B-2")]
public sealed class ChatPrototypeApiTests
{
    internal static string Root(string module = "social") => $"/__test/chat/v1/{module}/conversations";
    internal static string Target(string module = "social", Guid? conversation = null) => $"{Root(module)}/{conversation ?? ChatPrototypeTestHost.Conversation}";
    internal static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Theory]
    [InlineData(1, "learning")]
    [InlineData(2, "hobbies")]
    [InlineData(3, "social")]
    public async Task Authorized_modules_list_get_history_and_submit_through_HTTP(int actor, string module)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync((SyntheticActor)actor);
        var list = await Read<ChatPrototypePage<ChatPrototypeConversationDto>>(await host.Client.GetAsync(Root(module) + "?pageSize=16"));
        Assert.Equal(new[] { ChatPrototypeTestHost.Conversation, ChatPrototypeScenario.Second }, list.Items.Select(item => item.ConversationId));
        Assert.All(list.Items, item => Assert.Equal(module, item.Module));
        Assert.Null(list.NextCursor);
        var detail = await Read<ChatPrototypeConversationDto>(await host.Client.GetAsync(Target(module)));
        Assert.Equal(ChatPrototypeTestHost.Conversation, detail.ConversationId);
        Assert.Equal(module, detail.Module);
        var history = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(Target(module) + "/messages?pageSize=16"));
        Assert.Equal(3, history.Items.Count);
        Assert.All(history.Items, item => Assert.Equal(module, item.Module));
        var before = DateTimeOffset.UtcNow;
        var posted = await Read<ChatPrototypeMessageDto>(await host.Client.PostAsJsonAsync(Target(module) + "/messages", new ChatPrototypeSubmitDto("Hello")), HttpStatusCode.Created);
        Assert.Equal("Hello", posted.Text);
        Assert.True(posted.IsOwnMessage);
        Assert.NotEqual(Guid.Empty, posted.MessageId);
        Assert.InRange(posted.CreatedAtUtc, before, DateTimeOffset.UtcNow);
        var after = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(Target(module) + "/messages?pageSize=16"));
        Assert.Contains(after.Items, item => item == posted);
        Assert.Equal(4, after.Items.Count);
    }

    [Theory]
    [InlineData("  hello \t\nworld  ")]
    [InlineData("සිංහල தமிழ் 🦉 é \u0000")]
    [InlineData("a\r\nb")]
    public async Task Accepted_text_is_preserved_exactly_without_claiming_PostgreSQL_compatibility(string text)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Learning);
        var message = await Read<ChatPrototypeMessageDto>(await host.Client.PostAsJsonAsync(Target("learning") + "/messages", new ChatPrototypeSubmitDto(text)), HttpStatusCode.Created);
        Assert.Equal(text, message.Text);
        var history = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(Target("learning") + "/messages?pageSize=16"));
        Assert.Contains(history.Items, item => item.MessageId == message.MessageId && item.Text == text);
    }

    [Fact]
    public async Task History_order_is_descending_by_timestamp_then_id_with_deterministic_pages()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        var first = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(Target() + "/messages?pageSize=1"));
        Assert.Equal("synthetic-3", Assert.Single(first.Items).Text);
        Assert.NotNull(first.NextCursor);
        var replay = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(Target() + "/messages?pageSize=1&cursor=" + first.NextCursor));
        Assert.Equal("synthetic-2", Assert.Single(replay.Items).Text);
        Assert.Equal(first.Items[0].CreatedAtUtc, replay.Items[0].CreatedAtUtc);
        Assert.True(first.Items[0].MessageId.CompareTo(replay.Items[0].MessageId) > 0);
        var last = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(Target() + "/messages?pageSize=1&cursor=" + replay.NextCursor));
        Assert.Equal("synthetic-1", Assert.Single(last.Items).Text);
        Assert.True(last.Items[0].CreatedAtUtc < replay.Items[0].CreatedAtUtc);
        Assert.Null(last.NextCursor);
        var repeated = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(Target() + "/messages?pageSize=1&cursor=" + first.NextCursor));
        Assert.Equal(replay.Items, repeated.Items);
        Assert.True(first.Items[0].IsOwnMessage);
        Assert.False(replay.Items[0].IsOwnMessage);
    }

    [Fact]
    public async Task Conversation_list_filters_hidden_candidates_before_pagination_without_counts()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Hobbies);
        var first = await Read<ChatPrototypePage<ChatPrototypeConversationDto>>(await host.Client.GetAsync(Root("hobbies") + "?pageSize=1"));
        Assert.Equal(ChatPrototypeTestHost.Conversation, Assert.Single(first.Items).ConversationId);
        Assert.NotNull(first.NextCursor);
        var last = await Read<ChatPrototypePage<ChatPrototypeConversationDto>>(await host.Client.GetAsync(Root("hobbies") + "?pageSize=1&cursor=" + first.NextCursor));
        Assert.Equal(ChatPrototypeScenario.Second, Assert.Single(last.Items).ConversationId);
        Assert.Null(last.NextCursor);
        using var raw = JsonDocument.Parse(await (await host.Client.GetAsync(Root("hobbies") + "?pageSize=1")).Content.ReadAsStringAsync());
        Assert.Equal(new[] { "items", "nextCursor" }, raw.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(host.Evidence.Reads >= 9);
    }

    [Fact]
    public async Task Empty_history_and_no_authorized_candidates_return_empty_pages()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        var empty = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(Target(conversation: ChatPrototypeScenario.Second) + "/messages?pageSize=1"));
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextCursor);
        host.Evidence.Block();
        var list = await Read<ChatPrototypePage<ChatPrototypeConversationDto>>(await host.Client.GetAsync(Root() + "?pageSize=1"));
        Assert.Empty(list.Items);
        Assert.Null(list.NextCursor);
    }

    [Fact]
    public async Task DTOs_expose_only_approved_fields_and_no_private_actor_metadata()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        using var detail = JsonDocument.Parse(await (await host.Client.GetAsync(Target())).Content.ReadAsStringAsync());
        Assert.Equal(new[] { "conversationId", "module" }, detail.RootElement.EnumerateObject().Select(property => property.Name));
        using var posted = JsonDocument.Parse(await (await host.Client.PostAsJsonAsync(Target() + "/messages", new ChatPrototypeSubmitDto("safe"))).Content.ReadAsStringAsync());
        Assert.Equal(new[] { "messageId", "conversationId", "module", "text", "createdAtUtc", "isOwnMessage" }, posted.RootElement.EnumerateObject().Select(property => property.Name));
        var raw = posted.RootElement.GetRawText();
        foreach (var id in new[] { host.Identity!.Subject, host.Identity.Account, host.Identity.Profile })
            Assert.DoesNotContain(id.ToString(), raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_HTTP_posts_are_scoped_and_disposing_one_host_does_not_touch_another()
    {
        var first = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        await using var second = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => first.Client.PostAsJsonAsync(Target() + "/messages", new ChatPrototypeSubmitDto($"parallel-{i}"))));
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            foreach (var response in responses) response.Dispose();
            Assert.Equal(7, first.Scenario.MessageCount);
            Assert.Equal(3, second.Scenario.MessageCount);
            await first.Client.GetAsync(Target() + "/messages?pageSize=1");
            Assert.True(first.Scenario.CursorCount > 0);
            await first.DisposeAsync();
            Assert.True(first.Scenario.IsDisposed);
            Assert.Equal(0, first.Scenario.MessageCount);
            Assert.Equal(0, first.Scenario.CursorCount);
            var remaining = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await second.Client.GetAsync(Target() + "/messages?pageSize=16"));
            Assert.Equal(3, remaining.Items.Count);
        }
        finally { await first.DisposeAsync(); }
    }

    [Fact]
    public async Task All_synthetic_participants_have_independent_private_identities_between_modules()
    {
        var accounts = new HashSet<Guid>();
        var profiles = new HashSet<Guid>();
        foreach (var actor in new[] { SyntheticActor.Learning, SyntheticActor.Hobbies, SyntheticActor.Social })
        {
            await using var host = await ChatPrototypeTestHost.StartAsync(actor);
            var module = host.Identity!.Module.ToString().ToLowerInvariant();
            var participants = host.Scenario.Find(ChatPrototypeTestHost.Conversation)!.Participants;
            Assert.Equal(2, participants.Count);
            foreach (var participant in participants)
            {
                Assert.True(accounts.Add(participant.AccountId));
                Assert.True(profiles.Add(participant.ModuleProfileId));
                Assert.Equal(host.Identity.Module, participant.Module);
            }
            var history = await Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(Target(module) + "/messages?pageSize=16"));
            Assert.All(history.Items, message => Assert.Equal(module, message.Module));
            Assert.Single(history.Items, message => !message.IsOwnMessage);
        }
        Assert.Equal(6, accounts.Count);
        Assert.Equal(6, profiles.Count);
    }
}
