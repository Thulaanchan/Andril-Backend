using Andril.Common.Diary;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class DiaryCalendarResolverTests
{
    private sealed class Clock(DateTimeOffset instant) : TimeProvider
    {
        internal DateTimeOffset Instant = instant;
        internal int Calls;
        public override TimeZoneInfo LocalTimeZone => throw new InvalidOperationException("Local time must not be used.");
        public override DateTimeOffset GetUtcNow() { Calls++; return Instant; }
    }

    [Theory]
    [InlineData("2026-11-24T18:29:59Z", "Asia/Colombo", 2026, 11, 24)]
    [InlineData("2026-11-24T18:30:00Z", "Asia/Colombo", 2026, 11, 25)]
    [InlineData("2026-01-01T00:00:00Z", "America/Los_Angeles", 2025, 12, 31)]
    [InlineData("2026-12-31T10:00:00Z", "Pacific/Kiritimati", 2027, 1, 1)]
    [InlineData("2024-02-28T18:30:00Z", "Asia/Colombo", 2024, 2, 29)]
    [InlineData("2024-02-29T18:30:00Z", "Asia/Colombo", 2024, 3, 1)]
    [InlineData("2026-03-08T06:59:59Z", "America/New_York", 2026, 3, 8)]
    [InlineData("2026-03-08T07:00:00Z", "America/New_York", 2026, 3, 8)]
    [InlineData("2026-11-01T05:30:00Z", "America/New_York", 2026, 11, 1)]
    [InlineData("2026-11-01T06:30:00Z", "America/New_York", 2026, 11, 1)]
    [InlineData("2026-07-01T04:00:00Z", "America/New_York", 2026, 7, 1)]
    [InlineData("2026-12-01T04:00:00Z", "America/New_York", 2026, 11, 30)]
    public void Today_UsesSelectedZoneAtMidnightDstAndLeapBoundaries(string instant, string zone, int year, int month, int day)
    {
        var clock = new Clock(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(new DateOnly(year, month, day), new DiaryCalendarResolver(clock).ResolveDate(zone));
        Assert.Equal(1, clock.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Invalid/PrivateTimezone")]
    [InlineData("Eastern Standard Time")]
    [InlineData(" Asia/Colombo ")]
    public void MissingInvalidOrWindowsZone_IsUnavailableWithoutClockOrLocalFallback(string? zone)
    {
        var clock = new Clock(DateTimeOffset.UnixEpoch);
        Assert.Null(new DiaryCalendarResolver(clock).ResolveDate(zone));
        Assert.Equal(0, clock.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Invalid/PrivateTimezone")]
    [InlineData("Asia/Colombo")]
    [InlineData("America/Los_Angeles")]
    public void ExplicitHistoricalDate_IsLiteralWithoutClockOrTimezoneResolution(string? zone)
    {
        var clock = new Clock(DateTimeOffset.UnixEpoch); var resolver = new DiaryCalendarResolver(clock);
        foreach (var date in new[] { DateOnly.MinValue, new DateOnly(2024, 2, 29), DateOnly.MaxValue })
            Assert.Equal(date, resolver.ResolveDate(zone, date));
        Assert.Equal(0, clock.Calls);
    }

    [Fact]
    public void ZoneAndClockChanges_AffectFutureResolutionWithoutMovingOrMergingExistingEntries()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 24, 20, 0, 0, TimeSpan.Zero));
        var resolver = new DiaryCalendarResolver(clock);
        var owner = Guid.NewGuid();
        var first = new ModuleDiaryEntry(owner, "learning", resolver.ResolveDate("Asia/Colombo")!.Value);
        first.SetReflection(null, "Private original reflection");
        var reflection = first.Reflection;
        var second = new ModuleDiaryEntry(owner, "learning", resolver.ResolveDate("America/Los_Angeles")!.Value);
        clock.Instant = clock.Instant.AddDays(1);
        Assert.Equal(new DateOnly(2026, 11, 26), resolver.ResolveDate("Asia/Colombo"));
        Assert.Null(resolver.ResolveDate(null));
        Assert.Equal(new DateOnly(2026, 11, 25), first.Date);
        Assert.Equal(new DateOnly(2026, 11, 24), second.Date);
        Assert.Same(reflection, first.Reflection); Assert.Null(second.Reflection);
        Assert.Equal(first.Date, resolver.ResolveDate("America/Los_Angeles", first.Date));
    }

    [Fact]
    public void ProfileAndModuleEntriesRemainIndependentWithOneStatelessResolver()
    {
        var resolver = new DiaryCalendarResolver(new Clock(DateTimeOffset.UnixEpoch));
        var owners = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var entries = owners.SelectMany(owner => new[] { "learning", "hobbies", "social" }
            .Select(module => new ModuleDiaryEntry(owner, module, resolver.ResolveDate("Asia/Colombo")!.Value))).ToArray();
        entries[0].SetReflection(null, "Private Learning reflection");
        Assert.Equal(6, entries.Select(entry => (entry.OwnerModuleProfileId, entry.Module, entry.Date)).Distinct().Count());
        Assert.All(entries.Skip(1), entry => Assert.Null(entry.Reflection));
        Assert.All(entries, entry => Assert.True(entry.IsPrivate));
        Assert.Equal(new DateOnly(1970, 1, 1), entries[0].Date);
        // Calendar calculation grants no identity, access or cross-module retrieval permission.
    }

    [Fact]
    public void MissingClock_IsRejected() =>
        Assert.Throws<ArgumentNullException>("clock", () => new DiaryCalendarResolver(null!));
}
