using System.Text.Json;
using Andril.Common.Diary;

namespace Andril.Persistence.Repositories.Diary;

internal sealed class DiaryEntryRow
{
    public Guid OwnerModuleProfileId { get; private set; }
    public string Module { get; private set; } = null!;
    public DateOnly CalendarDate { get; private set; }
    public string? ReflectionTitle { get; private set; }
    public string? ReflectionBody { get; private set; }
    public string Plans { get; private set; } = "[]";

    // Explicit storage projection: domain values deliberately hide private content from JSON.
    private sealed record StoredPlan(Guid Id, string Title, string? Description);

    internal static DiaryEntryRow From(DiaryEntrySnapshot entry) => new()
    {
        OwnerModuleProfileId = entry.OwnerModuleProfileId, Module = entry.Module, CalendarDate = entry.Date,
        ReflectionTitle = entry.Reflection?.Title, ReflectionBody = entry.Reflection?.Body,
        Plans = JsonSerializer.Serialize(entry.Plans.Select(p => new StoredPlan(p.Id, p.Title, p.Description)))
    };

    internal ModuleDiaryEntry ToDomain()
    {
        var entry = new ModuleDiaryEntry(OwnerModuleProfileId, Module, CalendarDate);
        if (ReflectionBody is not null) entry.SetReflection(ReflectionTitle, ReflectionBody);
        else if (ReflectionTitle is not null) throw new InvalidOperationException("Invalid stored Diary reflection.");
        var plans = JsonSerializer.Deserialize<StoredPlan[]>(Plans) ?? throw new JsonException("Invalid stored Diary plans.");
        foreach (var plan in plans) entry.AddPlan(new DiaryPlan(plan.Id, plan.Title, plan.Description));
        return entry;
    }
    internal void ReplaceContent(DiaryEntryRow replacement)
    {
        if (OwnerModuleProfileId != replacement.OwnerModuleProfileId || Module != replacement.Module || CalendarDate != replacement.CalendarDate)
            throw new InvalidOperationException("Diary identity is immutable.");
        ReflectionTitle = replacement.ReflectionTitle; ReflectionBody = replacement.ReflectionBody; Plans = replacement.Plans;
    }
    public override string ToString() => "Private persisted Diary entry [scope/date/content omitted]";
}
