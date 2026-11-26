using Andril.Communication.Interfaces;

namespace Andril.Communication.Calls.Development;

/// <summary>Process-local atomic state/idempotency store, never production durability. Access only under Gate.</summary>
internal sealed class DevelopmentCallRepository : IDisposable
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal Dictionary<Guid, CallSession> Calls { get; } = [];
    internal Dictionary<(Guid Subject, ChatModule Module, Guid Account, Guid Profile, Guid Conversation, Guid Request), Guid> Requests { get; } = [];
    internal List<CallStateTransition> Transitions { get; } = [];
    internal Dictionary<string, string> Webhooks { get; } = new(StringComparer.Ordinal);
    internal Dictionary<(Guid Call, string Identity), DateTimeOffset> ProviderWatermarks { get; } = [];
    internal void Save(CallSession value)
    {
        if (!Calls.TryGetValue(value.CallId, out var prior) || prior.Version != value.Version)
            Transitions.Add(new(value.CallId, value.Status, value.Version));
        Calls[value.CallId] = value;
    }
    public void Dispose() => Gate.Dispose();
}
