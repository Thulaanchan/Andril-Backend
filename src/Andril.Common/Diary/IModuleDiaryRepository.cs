using System.Text.Json.Serialization;

namespace Andril.Common.Diary;

/// <summary>Private backend persistence contract. Every call resolves the current server-side
/// module profile and revalidates eligibility. Supplied entry IDs/module/date never authorize.
/// No HTTP, sharing, AI, discovery, account purge or retention contract is introduced.</summary>
public interface IModuleDiaryRepository
{
    Task<DiaryWriteOutcome> CreateAsync(ModuleDiaryEntry entry, CancellationToken cancellationToken = default);
    Task<DiaryReadResult> ReadAsync(string module, DateOnly date, CancellationToken cancellationToken = default);
    Task<DiaryCalendarResult> CalendarAsync(string module, DateOnly from, DateOnly through, CancellationToken cancellationToken = default);
    Task<DiaryWriteOutcome> EditAsync(ModuleDiaryEntry entry, CancellationToken cancellationToken = default);
    /// <summary>Explicit owner deletion permanently removes this entry only; recreation is allowed.
    /// Temporary deactivation is never a deletion command. Unknown commits require an authorized
    /// state read before any caller-decided retry; the repository never retries mutations.</summary>
    Task<DiaryWriteOutcome> DeleteAsync(string module, DateOnly date, CancellationToken cancellationToken = default);
}

public enum DiaryReadState { Available = 1, NotFound = 2, Denied = 3, Unavailable = 4 }
public enum DiaryWriteOutcome { Committed = 1, NotFound = 2, Conflict = 3, Denied = 4, Unavailable = 5, CommitUnknown = 6 }

public sealed class DiaryReadResult
{
    [JsonIgnore] public DiaryReadState State { get; }
    [JsonIgnore] public DiaryEntrySnapshot? Entry { get; }
    private DiaryReadResult(DiaryReadState state, DiaryEntrySnapshot? entry) { State = state; Entry = entry; }
    public static DiaryReadResult Available(DiaryEntrySnapshot entry) => new(DiaryReadState.Available, entry ?? throw new ArgumentNullException(nameof(entry)));
    public static DiaryReadResult Missing() => new(DiaryReadState.NotFound, null);
    public static DiaryReadResult Denied() => new(DiaryReadState.Denied, null);
    public static DiaryReadResult Unavailable() => new(DiaryReadState.Unavailable, null);
    public override string ToString() => "Private Diary read result [scope and content omitted]";
}

public sealed class DiaryCalendarResult
{
    [JsonIgnore] public DiaryReadState State { get; }
    [JsonIgnore] public IReadOnlyList<DateOnly>? Dates { get; }
    private DiaryCalendarResult(DiaryReadState state, IReadOnlyList<DateOnly>? dates) { State = state; Dates = dates; }
    public static DiaryCalendarResult Available(IEnumerable<DateOnly> dates) => new(DiaryReadState.Available, Array.AsReadOnly(dates.ToArray()));
    public static DiaryCalendarResult Denied() => new(DiaryReadState.Denied, null);
    public static DiaryCalendarResult Unavailable() => new(DiaryReadState.Unavailable, null);
    public override string ToString() => "Private Diary calendar result [scope and dates omitted]";
}
