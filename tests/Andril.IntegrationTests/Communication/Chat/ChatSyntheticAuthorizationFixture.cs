using System.Reflection;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat;

/// <summary>Test-only reflection adapter to the real internal policy; no production visibility changes.</summary>
public class ChatSyntheticEvidenceProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler!(targetMethod!, args);
}

internal sealed class ChatSyntheticAuthorizationFixture : IDisposable
{
    private static readonly Guid Account = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private SyntheticIdentity? fixedIdentity;
    private bool disposed;

    // Explicit synthetic evidence instants only; no production TTL or tolerance is proposed.
    internal static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = new(2030, 1, 1, 0, 0, 1, TimeSpan.Zero);
    private static readonly Type EvidenceType = typeof(IChatAccessAuthorizer).Assembly.GetType("Andril.Communication.Chat.ChatConversationAccessEvidence", true)!;
    internal SyntheticUser User { get; } = new();
    internal Guid AccountId { get; set; } = Account;
    internal IChatAccessAuthorizer Policy { get; }
    internal int Reads { get; private set; }
    internal object? LastEvidence { get; private set; }
    internal Func<object, object?> Transform { get; set; } = evidence => evidence;
    internal TaskCompletionSource<object?>? HoldNext { get; set; }

    internal ChatSyntheticAuthorizationFixture(bool missingReader = false, bool missingUser = false)
    {
        Policy = CreatePolicy(missingUser ? null : User, missingReader);
    }

    internal IChatAccessAuthorizer CreatePolicy(ICurrentUser? user, bool missingReader = false)
    {
        var assembly = typeof(IChatAccessAuthorizer).Assembly;
        var readerType = assembly.GetType("Andril.Communication.Interfaces.IChatConversationAccessEvidenceReader", true)!;
        var proxy = DispatchProxy.Create(readerType, typeof(ChatSyntheticEvidenceProxy));
        ((ChatSyntheticEvidenceProxy)proxy).Handler = (_, args) => Read(args!);
        var policyType = assembly.GetType("Andril.Communication.Chat.ChatConversationAccessPolicy", true)!;
        return Assert.IsAssignableFrom<IChatAccessAuthorizer>(Activator.CreateInstance(policyType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: [user, missingReader ? null : proxy, new FixedClock()], culture: null));
    }

    internal ChatActorContextIsolationContract Slot() => new(Policy, User);
    internal ChatConversation ConversationFor(ChatModule module, Guid conversation) => ChatConversation.Create(module, conversation,
        [ChatParticipantReference.Create(module, fixedIdentity?.Account ?? AccountId, fixedIdentity?.Profile ?? Profile(module, AccountId))]);

    // Legacy actor-slot cases deliberately exercise changing module/account pairs. Hosts never use this mapping.
    internal static Guid Profile(ChatModule module, Guid account) => (account == Account ? (int)module : (int)module + 3) switch
    {
        1 => Guid.Parse("40000000-0000-4000-8000-000000000001"),
        2 => Guid.Parse("40000000-0000-4000-8000-000000000002"),
        3 => Guid.Parse("40000000-0000-4000-8000-000000000003"),
        4 => Guid.Parse("40000000-0000-4000-8000-000000000004"),
        5 => Guid.Parse("40000000-0000-4000-8000-000000000005"),
        _ => Guid.Parse("40000000-0000-4000-8000-000000000006")
    };

    private object Read(object?[] args)
    {
        if (disposed || (fixedIdentity is not null &&
            (args[0] is not Guid subject || subject != fixedIdentity.Subject ||
             (ChatModule)args[1]! != fixedIdentity.Module || (Guid)args[2]! != SyntheticIdentity.Conversation)))
            return BridgeResult(Task.FromResult<object?>(null));
        Reads++;
        var module = (ChatModule)args[1]!;
        var evidence = Activator.CreateInstance(EvidenceType, nonPublic: true)!;
        Set(evidence, "RequestId", args[3]);
        Set(evidence, "AccountSubjectId", args[0]);
        Set(evidence, "AccountId", fixedIdentity?.Account ?? AccountId);
        Set(evidence, "ProfileAccountId", fixedIdentity?.Account ?? AccountId);
        Set(evidence, "ModuleProfileId", fixedIdentity?.Profile ?? Profile(module, AccountId));
        Set(evidence, "ProfileModule", module);
        Set(evidence, "Conversation", ConversationFor(module, (Guid)args[2]!));
        Set(evidence, "ObservedAtUtc", Now);
        Set(evidence, "ValidUntilUtc", Expiry);
        foreach (var flag in new[] { "IsCurrent", "IsProfileActive", "IsProfileEligible", "IsParticipationAuthorized",
            "IsModuleCommunicationEligible", "IsAdult", "HasSocialOptIn", "HasMutualMatch" }) Set(evidence, flag, true);
        Set(evidence, "IsBlocked", false);
        Set(evidence, "IsRevoked", false);
        LastEvidence = evidence;
        var held = HoldNext;
        HoldNext = null;
        var task = held?.Task ?? Task.FromResult(Transform(evidence));
        return BridgeResult(task);
    }

    private static object BridgeResult(Task<object?> task) => typeof(ChatSyntheticAuthorizationFixture).GetMethod(nameof(Bridge), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(EvidenceType).Invoke(null, [task])!;

    private static async Task<T?> Bridge<T>(Task<object?> task) where T : class => (T?)await task;

    internal static ChatSyntheticAuthorizationFixture ForIdentity(SyntheticIdentity? identity)
    {
        var fixture = new ChatSyntheticAuthorizationFixture { fixedIdentity = identity };
        fixture.User.UserId = identity?.Subject;
        fixture.User.IsAuthenticated = identity is not null;
        return fixture;
    }

    internal void Block() => SetFlag("IsBlocked", true);
    internal void Unmatch() => SetFlag("HasMutualMatch", false);
    internal void RevokeEligibility() => SetFlag("IsProfileEligible", false);
    internal void SetFlag(string flag, bool? value)
    {
        var previous = Transform;
        Transform = evidence =>
        {
            var current = previous(evidence);
            if (current is null) return null;
            Set(current, flag, value);
            return current;
        };
    }
    internal void RemoveEvidence() => Transform = _ => null;
    internal void StaleEvidence() => Transform = evidence => { Set(evidence, "ObservedAtUtc", Now.AddTicks(-1)); return evidence; };
    internal void ConflictingEvidence() => Transform = evidence => { Set(evidence, "ProfileAccountId", Guid.Empty); return evidence; };
    internal bool IsDisposed => disposed;

    public void Dispose()
    {
        disposed = true;
        User.UserId = null;
        User.IsAuthenticated = false;
        LastEvidence = null;
        HoldNext?.TrySetCanceled();
        HoldNext = null;
        Transform = _ => null;
    }

    public override string ToString() => nameof(ChatSyntheticAuthorizationFixture);

    internal static void Set(object evidence, string property, object? value) => evidence.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(evidence, value);

    internal sealed class SyntheticUser : ICurrentUser
    {
        public Guid? UserId { get; set; } = Guid.Parse("30000000-0000-4000-8000-000000000001");
        public bool IsAuthenticated { get; set; } = true;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

internal enum SyntheticActor { Anonymous, Learning, Hobbies, Social }

// Independent fake subjects/accounts/profiles, explicitly selected by test setup only.
// No lookup by HTTP input and no cross-module account linkage.
internal sealed record SyntheticIdentity(Guid Subject, Guid Account, Guid Profile, ChatModule Module)
{
    internal static readonly Guid Conversation = Guid.Parse("10000000-0000-4000-8000-000000000001");

    internal static SyntheticIdentity? Select(SyntheticActor actor) => actor switch
    {
        SyntheticActor.Learning => new(Guid.Parse("31000000-0000-4000-8000-000000000001"), Guid.Parse("21000000-0000-4000-8000-000000000001"), Guid.Parse("41000000-0000-4000-8000-000000000001"), ChatModule.Learning),
        SyntheticActor.Hobbies => new(Guid.Parse("32000000-0000-4000-8000-000000000002"), Guid.Parse("22000000-0000-4000-8000-000000000002"), Guid.Parse("42000000-0000-4000-8000-000000000002"), ChatModule.Hobbies),
        SyntheticActor.Social => new(Guid.Parse("33000000-0000-4000-8000-000000000003"), Guid.Parse("23000000-0000-4000-8000-000000000003"), Guid.Parse("43000000-0000-4000-8000-000000000003"), ChatModule.Social),
        _ => null
    };

    public override string ToString() => nameof(SyntheticIdentity);
}
