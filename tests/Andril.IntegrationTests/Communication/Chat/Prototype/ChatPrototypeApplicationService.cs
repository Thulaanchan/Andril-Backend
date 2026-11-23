using System.Globalization;
using System.Text.Json;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Andril.IntegrationTests.Communication.Chat.Prototype;

/// <summary>Proposed test-only HTTP operations. No persistence, delivery or production permission rules.</summary>
internal sealed class ChatPrototypeApplicationService(ChatPrototypeScenario scenario,
    IChatAccessAuthorizer authorizer, ICurrentUser user)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void MapRoutes(IEndpointRouteBuilder endpoints)
    {
        const string root = "/__test/chat/v1/{module}/conversations";
        endpoints.MapGet(root, context => Execute(context, "list"));
        endpoints.MapGet(root + "/{conversationId}", context => Execute(context, "detail"));
        endpoints.MapGet(root + "/{conversationId}/messages", context => Execute(context, "history"));
        endpoints.MapPost(root + "/{conversationId}/messages", context => Execute(context, "submit"));
    }

    private static Task Execute(HttpContext context, string operation) => context.RequestServices
        .GetRequiredService<ChatPrototypeApplicationService>().HandleAsync(context, operation);

    private async Task HandleAsync(HttpContext context, string operation)
    {
        var cancellation = context.RequestAborted;
        await scenario.Operations.WaitAsync(cancellation);
        try
        {
            if (!user.IsAuthenticated || user.UserId is null) { await Error(context, 401); return; }
            var module = context.Request.RouteValues["module"]?.ToString() switch
            {
                "learning" => ChatModule.Learning,
                "hobbies" => ChatModule.Hobbies,
                "social" => ChatModule.Social,
                _ => (ChatModule)0
            };
            if (!Enum.IsDefined(module) || scenario.Identity?.Module != module) { await Error(context, 404); return; }
            scenario.BeforeRead?.Invoke();
            if (operation == "list") { await List(context, module); return; }
            if (!Guid.TryParse(context.Request.RouteValues["conversationId"]?.ToString(), out var id))
            { await Error(context, 404); return; }
            var conversation = scenario.Find(id);
            var actor = await authorizer.AuthorizeConversationAsync(module, id, cancellation);
            if (conversation is null || actor is null) { await Error(context, 404); return; }
            if (operation == "detail") { await context.Response.WriteAsJsonAsync(Conversation(conversation), cancellation); return; }
            if (operation == "history") { await History(context, module, id, actor); return; }
            ChatPrototypeSubmitDto? input;
            try { input = await JsonSerializer.DeserializeAsync<ChatPrototypeSubmitDto>(context.Request.Body, Json, cancellation); }
            catch (JsonException) { await Error(context, 400); return; }
            if (input is null) { await Error(context, 400); return; }
            ChatMessage message;
            try
            {
                // The real domain factory resolves the canonical participant and validates text/MoodTone.
                var sender = ChatParticipantReference.Create(module, actor.AccountId, actor.ModuleProfileId);
                message = ChatMessage.Create(conversation, sender, input.Text, input.MoodTone);
            }
            catch (ArgumentException) { await Error(context, 400); return; }
            if (scenario.BeforeAppend is { } hook) await hook(cancellation);
            cancellation.ThrowIfCancellationRequested();
            // A deterministic local recheck, not a distributed/production revocation guarantee.
            var current = await authorizer.AuthorizeConversationAsync(module, id, cancellation);
            if (current is null || current != actor) { await Error(context, 404); return; }
            scenario.Append(message, cancellation);
            context.Response.StatusCode = 201;
            await context.Response.WriteAsJsonAsync(Message(message, current), cancellation);
        }
        finally { scenario.Operations.Release(); }
    }

    private async Task List(HttpContext context, ChatModule module)
    {
        if (!Paging(context.Request, module, null, out var size, out var cursor)) { await Error(context, 400); return; }
        var visible = new List<(ChatConversation Conversation, ChatAuthorizedActor Actor)>();
        foreach (var candidate in scenario.Candidates())
        {
            var actor = await authorizer.AuthorizeConversationAsync(module, candidate.ConversationId, context.RequestAborted);
            if (actor is not null) visible.Add((candidate, actor));
        }
        // Filter all finite candidates before pagination. No counts or offsets of hidden records are exposed.
        var remaining = visible.Where(item => cursor is null || item.Conversation.ConversationId.CompareTo(cursor.Position) > 0).ToArray();
        var page = remaining.Take(size).ToArray();
        var next = remaining.Length > size && page.Length > 0
            ? scenario.IssueCursor(page[^1].Actor, null, page[^1].Conversation.ConversationId, null) : null;
        await context.Response.WriteAsJsonAsync(new ChatPrototypePage<ChatPrototypeConversationDto>(
            page.Select(item => Conversation(item.Conversation)).ToArray(), next), context.RequestAborted);
    }

    private async Task History(HttpContext context, ChatModule module, Guid id, ChatAuthorizedActor actor)
    {
        if (!Paging(context.Request, module, id, out var size, out var cursor)) { await Error(context, 400); return; }
        var remaining = scenario.History(id).Where(item => cursor is null || item.CreatedAtUtc < cursor.Timestamp
            || (item.CreatedAtUtc == cursor.Timestamp && item.MessageId.CompareTo(cursor.Position) < 0)).ToArray();
        var page = remaining.Take(size).ToArray();
        var next = remaining.Length > size && page.Length > 0
            ? scenario.IssueCursor(actor, id, page[^1].MessageId, page[^1].CreatedAtUtc) : null;
        await context.Response.WriteAsJsonAsync(new ChatPrototypePage<ChatPrototypeMessageDto>(
            page.Select(item => Message(item, actor)).ToArray(), next), context.RequestAborted);
    }

    private bool Paging(HttpRequest request, ChatModule module, Guid? conversation, out int size, out ChatPrototypeScenario.Cursor? cursor)
    {
        cursor = null;
        size = 0;
        var sizes = request.Query["pageSize"];
        if (sizes.Count != 1 || !int.TryParse(sizes[0], NumberStyles.None, CultureInfo.InvariantCulture, out size)
            || size <= 0 || size > ChatPrototypeScenario.PagingSafeguard) return false;
        if (!request.Query.ContainsKey("cursor")) return true;
        var tokens = request.Query["cursor"];
        if (tokens.Count != 1 || string.IsNullOrEmpty(tokens[0])) return false;
        cursor = scenario.ResolveCursor(tokens[0]!, module, conversation);
        return cursor is not null;
    }

    private static ChatPrototypeConversationDto Conversation(ChatConversation conversation) =>
        new(conversation.ConversationId, conversation.Module.ToString().ToLowerInvariant());
    private static ChatPrototypeMessageDto Message(ChatMessage message, ChatAuthorizedActor actor) =>
        new(message.MessageId, message.ConversationId, message.Module.ToString().ToLowerInvariant(), message.Text,
            message.CreatedAtUtc, message.Sender.AccountId == actor.AccountId && message.Sender.ModuleProfileId == actor.ModuleProfileId);

    internal static Task Error(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ChatPrototypeError(status switch
        {
            400 => "invalid_request",
            401 => "unauthenticated",
            404 => "unavailable",
            _ => "unexpected_failure"
        }, status switch
        {
            400 => "Invalid request.",
            401 => "Authentication required.",
            404 => "Resource unavailable.",
            _ => "Request failed."
        }, status), context.RequestAborted);
    }
}
