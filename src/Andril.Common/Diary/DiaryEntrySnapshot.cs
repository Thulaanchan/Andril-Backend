using System.Text.Json.Serialization;

namespace Andril.Common.Diary;

/// <summary>
/// Detached owner-only offline Diary view. No public transport/export/AI/matching/discovery/
/// notification contract is implied. Construction occurs inside the isolated service boundary;
/// possession is not an ongoing authorization grant. No cross-module lookup or dispatch occurs.
/// </summary>
public sealed class DiaryEntrySnapshot
{
    [JsonIgnore] public Guid OwnerModuleProfileId { get; }
    [JsonIgnore] public string Module { get; }
    [JsonIgnore] public DateOnly Date { get; }
    [JsonIgnore] public DiaryReflection? Reflection { get; }
    [JsonIgnore] public IReadOnlyList<DiaryPlan> Plans { get; }
    public bool IsPrivate => true;

    internal DiaryEntrySnapshot(ModuleDiaryEntry entry)
    {
        OwnerModuleProfileId = entry.OwnerModuleProfileId; Module = entry.Module; Date = entry.Date;
        // Reflection and plan values are immutable; copy the collection to detach its shape.
        Reflection = entry.Reflection; Plans = Array.AsReadOnly(entry.Plans.ToArray());
    }

    public override string ToString() => "Private Diary snapshot [owner, module, date and content omitted]";
}
