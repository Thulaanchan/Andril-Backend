namespace Andril.Persistence.Repositories.Hobbies;

// Unregistered storage records: no change to the production Map model or migration snapshot.
internal sealed class MapAttractionLifecycleRow
{
    public Guid AttractionId { get; set; }
    public bool IsActive { get; set; }
    public long Version { get; set; }
    public long OperationSequence { get; set; }
    public Guid? LastReceiptId { get; set; }
}

internal sealed class MapAttractionLifecycleReceiptRow
{
    public Guid ReceiptId { get; set; }
    public Guid ReviewerSubjectId { get; set; }
    public Guid RequestId { get; set; }
    public Guid AttractionId { get; set; }
    public bool RequestedIsActive { get; set; }
    public long ExpectedVersion { get; set; }
    public bool PreviousIsActive { get; set; }
    public long PreviousVersion { get; set; }
    public bool ResultIsActive { get; set; }
    public long ResultVersion { get; set; }
    public long PreviousOperationSequence { get; set; }
    public long ResultOperationSequence { get; set; }
    public short Outcome { get; set; }
    public DateTimeOffset AcceptedAtUtc { get; set; }
}

internal sealed class MapAttractionLifecycleAuditRow
{
    public Guid ReceiptId { get; set; }
    public Guid ReviewerSubjectId { get; set; }
    public Guid RequestId { get; set; }
    public Guid AttractionId { get; set; }
    public bool RequestedIsActive { get; set; }
    public long ExpectedVersion { get; set; }
    public bool PreviousIsActive { get; set; }
    public long PreviousVersion { get; set; }
    public bool ResultIsActive { get; set; }
    public long ResultVersion { get; set; }
    public long PreviousOperationSequence { get; set; }
    public long ResultOperationSequence { get; set; }
    public short Outcome { get; set; }
    public DateTimeOffset AcceptedAtUtc { get; set; }
}
