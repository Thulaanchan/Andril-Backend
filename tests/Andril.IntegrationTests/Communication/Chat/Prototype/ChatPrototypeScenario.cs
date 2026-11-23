using System.Reflection;
using System.Security.Cryptography;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;

namespace Andril.IntegrationTests.Communication.Chat.Prototype;

/// <summary>Host-owned synthetic data and opaque cursor registry, never production persistence.</summary>
internal sealed class ChatPrototypeScenario : IDisposable
{
    internal static readonly Guid Second = Guid.Parse("10000000-0000-4000-8000-000000000002");
    internal static readonly Guid Hidden = Guid.Parse("10000000-0000-4000-8000-000000000000");
    // Test-scenario safeguard only; not a proposed product pagination maximum.
    internal const int PagingSafeguard = 16;
    private readonly object sync = new();
    private readonly Dictionary<Guid, ChatConversation> conversations = new();
    private readonly List<ChatMessage> messages = [];
    private readonly Dictionary<string, Cursor> cursors = new(StringComparer.Ordinal);
    private bool disposed;
    internal SemaphoreSlim Operations { get; } = new(1, 1);
    internal SyntheticIdentity? Identity { get; }
    internal Func<CancellationToken, Task>? BeforeAppend { get; set; }
    internal Action? BeforeRead { get; set; }

    internal ChatPrototypeScenario(SyntheticIdentity? identity, ChatSyntheticAuthorizationFixture evidence)
    {
        Identity = identity;
        if (identity is null) return;
        var own = ChatParticipantReference.Create(identity.Module, identity.Account, identity.Profile);
        var peerIds = identity.Module switch
        {
            ChatModule.Learning => ("29000000-0000-4000-8000-000000000001", "49000000-0000-4000-8000-000000000001"),
            ChatModule.Hobbies => ("29000000-0000-4000-8000-000000000002", "49000000-0000-4000-8000-000000000002"),
            ChatModule.Social => ("29000000-0000-4000-8000-000000000003", "49000000-0000-4000-8000-000000000003"),
            _ => throw new InvalidOperationException("Unknown synthetic module.")
        };
        var peer = ChatParticipantReference.Create(identity.Module, Guid.Parse(peerIds.Item1), Guid.Parse(peerIds.Item2));
        foreach (var id in new[] { SyntheticIdentity.Conversation, Second, Hidden })
        {
            var conversation = ChatConversation.Create(identity.Module, id, id == Hidden ? [peer] : [own, peer]);
            conversations.Add(id, conversation);
            evidence.RegisterConversation(conversation);
        }
        // Explicit seed metadata exercises timestamp ties. Writes use the public domain creation API.
        var factory = typeof(ChatMessage).GetMethod("CreateFromServerValues", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (var i = 1; i <= 3; i++)
        {
            var id = Guid.Parse($"50000000-0000-4000-8000-{i:D12}");
            var at = new DateTimeOffset(2030, 1, 1, 0, 0, i == 1 ? 0 : 1, TimeSpan.Zero);
            messages.Add((ChatMessage)factory.Invoke(null, [identity.Module, id, SyntheticIdentity.Conversation,
                conversations[SyntheticIdentity.Conversation], i == 2 ? peer : own, $"synthetic-{i}", at, null])!);
        }
    }

    internal ChatConversation[] Candidates()
    {
        lock (sync) { EnsureActive(); return conversations.Values.OrderBy(item => item.ConversationId).ToArray(); }
    }

    internal ChatConversation? Find(Guid id)
    {
        lock (sync) { EnsureActive(); return conversations.GetValueOrDefault(id); }
    }

    internal ChatMessage[] History(Guid conversation)
    {
        lock (sync)
        {
            EnsureActive();
            return messages.Where(item => item.ConversationId == conversation)
                .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.MessageId).ToArray();
        }
    }

    internal void Append(ChatMessage message, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            messages.Add(message);
        }
    }

    internal int MessageCount { get { lock (sync) return messages.Count; } }
    internal int CursorCount { get { lock (sync) return cursors.Count; } }
    internal bool IsDisposed { get { lock (sync) return disposed; } }

    // Random handles encode no identity, position or message content; all bindings stay server-side.
    internal string IssueCursor(ChatAuthorizedActor actor, Guid? conversation, Guid position, DateTimeOffset? timestamp)
    {
        lock (sync)
        {
            EnsureActive();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            cursors.Add(token, new Cursor(actor.VerifiedSubjectId, actor.AccountId, actor.ModuleProfileId, actor.Module, conversation, position, timestamp));
            return token;
        }
    }

    internal Cursor? ResolveCursor(string token, ChatModule module, Guid? conversation)
    {
        lock (sync)
        {
            EnsureActive();
            return token.Length == 64 && cursors.TryGetValue(token, out var cursor) && Identity is { } identity
                && cursor.Subject == identity.Subject && cursor.Account == identity.Account && cursor.Profile == identity.Profile && cursor.Module == module
                && cursor.Conversation == conversation ? cursor : null;
        }
    }

    internal sealed record Cursor(Guid Subject, Guid Account, Guid Profile, ChatModule Module, Guid? Conversation,
        Guid Position, DateTimeOffset? Timestamp)
    {
        public override string ToString() => nameof(Cursor);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            conversations.Clear(); messages.Clear(); cursors.Clear();
            BeforeAppend = null; BeforeRead = null;
        }
        Operations.Dispose();
    }

    private void EnsureActive() => ObjectDisposedException.ThrowIf(disposed, this);
    public override string ToString() => nameof(ChatPrototypeScenario);
}
