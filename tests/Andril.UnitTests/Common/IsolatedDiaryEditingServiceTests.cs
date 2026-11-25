using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class IsolatedDiaryEditingServiceTests
{
    private sealed class Context : IPrivateModuleAccess, IPrivateProfileService
    {
        internal readonly Guid Owner = Guid.NewGuid();
        internal readonly DateOnly Date = new(2024, 2, 29);
        internal int GateCalls, ProfileCalls;
        internal readonly List<string> Modules = [];
        internal ModuleDiaryEntry Entry(string module = "learning")
        {
            var entry = new ModuleDiaryEntry(Owner, module, Date);
            entry.SetReflection("original", "private reflection");
            entry.AddPlan(new(Guid.NewGuid(), "first", "description"));
            entry.AddPlan(new(Guid.NewGuid(), "second")); return entry;
        }
        public Task RequireAsync(string module, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); GateCalls++; Modules.Add(module); return Task.CompletedTask; }
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); ProfileCalls++; return action(new(Owner, module, DateTimeOffset.UnixEpoch)); }
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("learning")] [InlineData("hobbies")] [InlineData("social")]
    public async Task OwnerContentEditingAndRemovalPreserveScopeOtherContentAndSnapshots(string module)
    {
        var c = new Context(); var service = new IsolatedDiaryEditingService(c, c); var entry = c.Entry(module);
        var oldReflection = entry.Reflection!; var oldPlans = entry.Plans; var first = oldPlans[0]; var second = oldPlans[1];
        await service.EditReflectionAsync(entry, "edited", "replacement");
        Assert.Equal("replacement", entry.Reflection!.Body); Assert.Equal("private reflection", oldReflection.Body); Assert.Same(oldPlans, entry.Plans);
        var editedReflection = entry.Reflection;
        await service.EditPlanAsync(entry, first.Id, "changed", "new description");
        Assert.Equal("changed", entry.Plans[0].Title); Assert.Equal(first.Id, entry.Plans[0].Id);
        Assert.Equal("first", first.Title); Assert.Same(second, entry.Plans[1]); Assert.Same(editedReflection, entry.Reflection);
        await service.RemovePlanAsync(entry, first.Id); Assert.Same(second, Assert.Single(entry.Plans));
        await service.RemoveReflectionAsync(entry); Assert.Null(entry.Reflection); Assert.Same(second, Assert.Single(entry.Plans));
        Assert.Equal(c.Owner, entry.OwnerModuleProfileId); Assert.Equal(module, entry.Module); Assert.Equal(c.Date, entry.Date);
        Assert.Equal(2, oldPlans.Count); Assert.True(entry.IsPrivate);
        Assert.Equal(8, c.GateCalls); Assert.Equal(4, c.ProfileCalls); Assert.All(c.Modules, m => Assert.Equal(module, m));
    }

    [Fact]
    public async Task ExistingValidationRejectsChangesWithoutPartialMutation()
    {
        var c = new Context(); var s = new IsolatedDiaryEditingService(c, c); var entry = c.Entry();
        var reflection = entry.Reflection; var plans = entry.Plans;
        await Assert.ThrowsAsync<ArgumentException>(() => s.EditReflectionAsync(entry, null, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => s.EditPlanAsync(entry, plans[0].Id, " "));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => s.RemovePlanAsync(entry, Guid.NewGuid()));
        Assert.Same(reflection, entry.Reflection); Assert.Same(plans, entry.Plans); Assert.Equal(c.Date, entry.Date);
        var empty = new ModuleDiaryEntry(c.Owner, "learning", c.Date);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.EditReflectionAsync(empty, null, "body"));
        Assert.Null(empty.Reflection);
    }

    [Fact]
    public async Task MissingDependenciesAndEntriesAreRejected()
    {
        var c = new Context();
        Assert.Throws<ArgumentNullException>(() => new IsolatedDiaryEditingService(null!, c));
        Assert.Throws<ArgumentNullException>(() => new IsolatedDiaryEditingService(c, null!));
        var s = new IsolatedDiaryEditingService(c, c);
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.EditReflectionAsync(null!, null, "body"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.RemoveReflectionAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.EditPlanAsync(null!, Guid.NewGuid(), "title"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.RemovePlanAsync(null!, Guid.NewGuid()));
        Assert.Equal(0, c.GateCalls); Assert.Equal(0, c.ProfileCalls);
    }
}
