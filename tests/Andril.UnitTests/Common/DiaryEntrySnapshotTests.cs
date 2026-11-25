using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class DiaryEntrySnapshotTests
{
    private sealed class Context : IPrivateModuleAccess, IPrivateProfileService
    {
        internal Guid Owner = Guid.NewGuid();
        internal readonly List<string> Calls = [];
        public Task RequireAsync(string module, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls.Add("gate:" + module); return Task.CompletedTask; }
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls.Add("profile:" + module); return action(new(Owner, module, DateTimeOffset.UnixEpoch)); }
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("learning")] [InlineData("hobbies")] [InlineData("social")]
    public async Task AuthorizedReadReturnsDetachedPrivateSnapshotWithoutChangingEntry(string module)
    {
        var c = new Context(); var service = new IsolatedDiaryEditingService(c, c);
        var date = new DateOnly(2024, 2, 29); var entry = new ModuleDiaryEntry(c.Owner, module, date);
        entry.SetReflection("private-title", "private-body"); var plan = new DiaryPlan(Guid.NewGuid(), "private-plan"); entry.AddPlan(plan);
        var snapshot = await service.ReadSnapshotAsync(entry);
        Assert.Equal(c.Owner, snapshot.OwnerModuleProfileId); Assert.Equal(module, snapshot.Module); Assert.Equal(date, snapshot.Date);
        Assert.Same(entry.Reflection, snapshot.Reflection); Assert.Same(plan, Assert.Single(snapshot.Plans)); Assert.NotSame(entry.Plans, snapshot.Plans);
        Assert.Equal(new[] { "gate:" + module, "profile:" + module, "gate:" + module }, c.Calls);
        Assert.Equal("{\"IsPrivate\":true}", JsonSerializer.Serialize(snapshot));
        Assert.DoesNotContain(c.Owner.ToString(), snapshot.ToString());
        foreach (var value in new[] { "private-title", "private-body", "private-plan" }) Assert.DoesNotContain(value, snapshot.ToString());
        entry.EditReflection(null, "edited-body"); entry.RemovePlan(plan.Id);
        Assert.Equal("private-body", snapshot.Reflection!.Body); Assert.Same(plan, Assert.Single(snapshot.Plans));
        Assert.Throws<NotSupportedException>(() => ((IList<DiaryPlan>)snapshot.Plans).Clear());
    }

    [Fact]
    public async Task EmptyEntryReadPreservesDateAndEmptyContent()
    {
        var c = new Context(); var entry = new ModuleDiaryEntry(c.Owner, "learning", DateOnly.MinValue);
        var snapshot = await new IsolatedDiaryEditingService(c, c).ReadSnapshotAsync(entry);
        Assert.Null(snapshot.Reflection); Assert.Empty(snapshot.Plans); Assert.True(snapshot.IsPrivate); Assert.Equal(DateOnly.MinValue, snapshot.Date);
        Assert.Null(entry.Reflection); Assert.Empty(entry.Plans);
    }

    [Fact]
    public async Task ReadsForDifferentOwnersAndModulesNeverMergeContent()
    {
        var c = new Context(); var service = new IsolatedDiaryEditingService(c, c); var snapshots = new List<DiaryEntrySnapshot>();
        foreach (var owner in new[] { Guid.NewGuid(), Guid.NewGuid() })
        foreach (var module in new[] { "learning", "hobbies", "social" })
        {
            c.Owner = owner; var entry = new ModuleDiaryEntry(owner, module, new(2026, 11, 25));
            entry.SetReflection(null, owner + ":" + module); snapshots.Add(await service.ReadSnapshotAsync(entry));
        }
        Assert.Equal(6, snapshots.Select(s => (s.OwnerModuleProfileId, s.Module, s.Date)).Distinct().Count());
        Assert.All(snapshots, s => Assert.Equal(s.OwnerModuleProfileId + ":" + s.Module, s.Reflection!.Body));
    }
}
