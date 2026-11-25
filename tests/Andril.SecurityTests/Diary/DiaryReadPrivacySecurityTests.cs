using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Xunit;

namespace Andril.SecurityTests.Diary;

public sealed class DiaryReadPrivacySecurityTests
{
    private sealed class Context : IPrivateModuleAccess, IPrivateProfileService, IPrivateProfileCompletionReader
    {
        internal readonly Guid Owner = Guid.NewGuid();
        internal string Defect = "";
        internal bool Active = true;
        internal int Gates, Profiles;
        internal Action? DuringProfile;
        public Task RequireAsync(string module, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Gates++;
            if (!Active || Defect == "revoked" && Gates % 2 == 0) throw new ProfileAccessDeniedException();
            if (Defect == "unavailable") throw new ProfileContractPendingException();
            return Task.CompletedTask;
        }
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Profiles++; DuringProfile?.Invoke();
            return action(new(Defect == "foreign-owner" ? Guid.NewGuid() : Defect == "empty-owner" ? Guid.Empty : Owner,
                Defect == "foreign-module" ? "social" : module, DateTimeOffset.UnixEpoch));
        }
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default) => Task.FromResult(new ProfileCompletionStatus(null, true));
        internal ModuleDiaryEntry Entry()
        {
            var entry = new ModuleDiaryEntry(Owner, "learning", new(2024, 2, 29));
            entry.SetReflection("PRIVATE-TITLE", "PRIVATE-BODY"); entry.AddPlan(new(Guid.NewGuid(), "PRIVATE-PLAN")); return entry;
        }
    }

    [Theory]
    [InlineData("foreign-owner")] [InlineData("foreign-module")] [InlineData("empty-owner")] [InlineData("revoked")]
    public async Task ForeignScopeAndRevocationNeverReleasePrivateSnapshot(string defect)
    {
        var c = new Context { Defect = defect }; var entry = c.Entry(); var reflection = entry.Reflection; var plans = entry.Plans;
        var error = await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => new IsolatedDiaryEditingService(c, c).ReadSnapshotAsync(entry));
        Assert.Same(reflection, entry.Reflection); Assert.Same(plans, entry.Plans);
        Assert.DoesNotContain("PRIVATE", error.ToString()); Assert.DoesNotContain(c.Owner.ToString(), error.ToString());
    }

    [Fact]
    public async Task InactiveAccessPreservesRecordsAndRequiresFreshAuthorizationAfterReactivation()
    {
        var c = new Context(); var entry = c.Entry(); var service = new IsolatedDiaryEditingService(c, c);
        var first = await service.ReadSnapshotAsync(entry); var reflection = entry.Reflection; var plans = entry.Plans;
        c.Active = false;
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => service.ReadSnapshotAsync(entry));
        Assert.Equal(1, c.Profiles); Assert.Same(reflection, entry.Reflection); Assert.Same(plans, entry.Plans);
        c.Active = true; var next = await service.ReadSnapshotAsync(entry);
        Assert.Equal(2, c.Profiles); Assert.NotSame(first, next); Assert.Equal(first.Reflection!.Body, next.Reflection!.Body);
        Assert.Equal(first.Date, next.Date); Assert.Equal(first.Module, next.Module);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationPreventsSnapshotReleaseWithoutMutatingEntry(bool duringProfile)
    {
        var c = new Context(); var entry = c.Entry(); var reflection = entry.Reflection; var plans = entry.Plans;
        using var cts = new CancellationTokenSource(); if (duringProfile) c.DuringProfile = cts.Cancel; else cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IsolatedDiaryEditingService(c, c).ReadSnapshotAsync(entry, cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken); Assert.Same(reflection, entry.Reflection); Assert.Same(plans, entry.Plans);
    }

    [Fact]
    public async Task PendingProductionGateAndUnavailableAuthorityFailClosed()
    {
        var c = new Context(); var entry = c.Entry();
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => new IsolatedDiaryEditingService(new PrivateModuleAccess(c), c).ReadSnapshotAsync(entry));
        Assert.Equal(0, c.Profiles);
        c.Defect = "unavailable";
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => new IsolatedDiaryEditingService(c, c).ReadSnapshotAsync(entry));
        Assert.Equal("PRIVATE-BODY", entry.Reflection!.Body); Assert.Single(entry.Plans);
    }

    [Fact]
    public async Task DefaultSerializationAndDiagnosticsDoNotForwardPrivateContext()
    {
        var c = new Context(); var entry = c.Entry();
        var snapshot = await new IsolatedDiaryEditingService(c, c).ReadSnapshotAsync(entry);
        Assert.Equal("{\"IsPrivate\":true}", JsonSerializer.Serialize(snapshot));
        foreach (var text in new[] { JsonSerializer.Serialize(new { diary = snapshot }), snapshot.ToString() })
        {
            Assert.DoesNotContain("PRIVATE-", text); Assert.DoesNotContain(c.Owner.ToString(), text);
            Assert.DoesNotContain("learning", text); Assert.DoesNotContain("2024", text);
        }
        Assert.All(typeof(DiaryEntrySnapshot).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.Empty(typeof(DiaryEntrySnapshot).GetConstructors());
    }
}
