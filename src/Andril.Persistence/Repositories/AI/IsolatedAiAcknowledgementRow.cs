using Andril.AI.ChatBox;

namespace Andril.Persistence.Repositories.AI;

internal sealed class IsolatedAiAcknowledgementRow
{
    public Guid AccountId { get; set; }
    public Guid ModuleProfileId { get; set; }
    public AiModule Module { get; set; }
    public Guid Generation { get; set; }
    public long Version { get; set; }
    public string PolicyVersion { get; set; } = "";
    public bool Acknowledged { get; set; }
    public Guid LastOperationId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public override string ToString() => "AI acknowledgement row [private evidence omitted]";
}
