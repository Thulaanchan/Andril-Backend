using System.Text.Json.Serialization;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Diary;

/// <summary>
/// CF-13 / DIA-R01-R10: private in-memory daily content for one fixed module profile/date.
/// The backend's social identifier represents the third product module, not a fourth identity.
/// Supplied profile IDs are not authentication. M2 must authorize ownership and eligibility;
/// persistence must enforce module/profile/date uniqueness and concurrency. The selected date
/// is supplied literally or resolved offline using the explicitly selected profile timezone.
/// No publishing, AI ingestion, rewards, cross-module transfer or external requests occur.
/// </summary>
public sealed class ModuleDiaryEntry
{
    [JsonIgnore]
    public Guid OwnerModuleProfileId { get; }
    [JsonIgnore]
    public string Module { get; }
    [JsonIgnore]
    public DateOnly Date { get; }
    public bool IsPrivate => true;
    [JsonIgnore]
    public DiaryReflection? Reflection { get; private set; }
    [JsonIgnore]
    public IReadOnlyList<DiaryPlan> Plans { get; private set; }

    public ModuleDiaryEntry(Guid ownerModuleProfileId, string module, DateOnly date)
    {
        RequireScope(ownerModuleProfileId, module);
        OwnerModuleProfileId = ownerModuleProfileId;
        Module = module;
        Date = date;
        Plans = Array.AsReadOnly(Array.Empty<DiaryPlan>());
    }

    /// <summary>
    /// CF-13 / DEC-M1-05: create a new private in-memory entry for a literal explicit date,
    /// or today's date from the resolver's injected UTC clock and selected IANA timezone.
    /// Return null when implicit date resolution is unavailable; never fall back to local time.
    /// Scope references are structural inputs, not authorization. This never loads, rekeys,
    /// merges or changes an existing entry, persists a preference or grants module access.
    /// The existing resolver preserves explicit dates even without timezone evidence.
    /// </summary>
    public static ModuleDiaryEntry? CreateOffline(Guid ownerModuleProfileId, string module,
        DiaryCalendarResolver calendar, string? ianaTimeZone, DateOnly? explicitDate = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        RequireScope(ownerModuleProfileId, module);
        var date = calendar.ResolveDate(ianaTimeZone, explicitDate);
        return date.HasValue ? new ModuleDiaryEntry(ownerModuleProfileId, module, date.Value) : null;
    }

    private static void RequireScope(Guid ownerModuleProfileId, string module)
    {
        if (ownerModuleProfileId == Guid.Empty)
            throw new ArgumentException("A module-profile owner ID is required.", nameof(ownerModuleProfileId));
        if (!ModuleNames.IsValid(module))
            throw new ArgumentException("Unsupported Diary module.", nameof(module));
    }

    /// <summary>Explicitly set or replace the single reflection after its domain validation.
    /// Replacing it changes neither daily identity nor plans; no history is implied.</summary>
    public void SetReflection(string? title, string body)
    {
        var replacement = new DiaryReflection(title, body);
        Reflection = replacement;
    }

    /// <summary>Edit the existing reflection without creating one implicitly.</summary>
    public void EditReflection(string? title, string body)
    {
        var current = Reflection ?? throw new InvalidOperationException("No reflection exists in this daily entry.");
        var replacement = current.WithContent(title, body);
        Reflection = replacement;
    }

    /// <summary>Remove only the in-memory reflection; plans remain. No persistent purge policy.</summary>
    public void RemoveReflection() => Reflection = null;

    public void AddPlan(DiaryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Plans.Any(existing => existing.Id == plan.Id))
            throw new ArgumentException("The plan identity already exists in this daily entry.", nameof(plan));
        if (Plans.Count >= 20)
            throw new InvalidOperationException("A daily entry cannot contain more than 20 plans.");
        var next = Plans.Append(plan).ToArray();
        Plans = Array.AsReadOnly(next);
    }

    public void EditPlan(Guid planId, string title, string? description = null)
    {
        var index = FindPlan(planId);
        var replacement = Plans[index].WithContent(title, description);
        var next = Plans.ToArray();
        next[index] = replacement;
        Plans = Array.AsReadOnly(next);
    }

    /// <summary>Remove only the selected in-memory plan; reflection and other plans remain.
    /// This does not specify physical deletion, retention or recovery.</summary>
    public void RemovePlan(Guid planId)
    {
        var index = FindPlan(planId);
        var next = Plans.Where((_, i) => i != index).ToArray();
        Plans = Array.AsReadOnly(next);
    }

    private int FindPlan(Guid planId)
    {
        if (planId == Guid.Empty)
            throw new ArgumentException("A Diary plan ID is required.", nameof(planId));
        for (var i = 0; i < Plans.Count; i++)
            if (Plans[i].Id == planId) return i;
        throw new KeyNotFoundException("The plan is not present in this daily entry.");
    }

    public override string ToString() => "Private module Diary entry [owner, module, date and content omitted]";
}
