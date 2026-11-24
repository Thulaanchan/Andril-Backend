namespace Andril.Common.Diary;

/// <summary>
/// DEC-M1-05 offline calendar calculation only. The caller supplies an explicitly selected
/// IANA timezone after separately resolving authorized profile context. No profile/entry store,
/// authorization, timezone preference persistence, local-time fallback or production registration.
/// Existing entry dates are never moved, merged or recalculated by this stateless resolver.
/// </summary>
public sealed class DiaryCalendarResolver
{
    private readonly TimeProvider clock;

    public DiaryCalendarResolver(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    /// <summary>
    /// Preserve an explicit calendar date literally. Otherwise resolve today from server UTC
    /// and the selected IANA zone. Null means timezone evidence/rules are unavailable, never
    /// permission to use the server's local timezone. Unexpected clock failures propagate.
    /// The zone is supplied per call; a changed selection affects future results only.
    /// </summary>
    public DateOnly? ResolveDate(string? ianaTimeZone, DateOnly? explicitDate = null)
    {
        if (explicitDate.HasValue) return explicitDate.Value;
        if (string.IsNullOrWhiteSpace(ianaTimeZone) ||
            !TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZone, out _)) return null;
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone);
        }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    }
}
