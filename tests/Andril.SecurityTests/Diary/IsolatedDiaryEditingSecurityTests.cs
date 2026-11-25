using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Xunit;

namespace Andril.SecurityTests.Diary;

public sealed class IsolatedDiaryEditingSecurityTests
{
    // Synthetic backend context is private to tests. No production authority is supplied.
    private sealed class Context : IPrivateModuleAccess, IPrivateProfileService, IPrivateProfileCompletionReader
    {
        internal readonly Guid Owner = Guid.NewGuid();
        internal bool Active = true;
        internal string Defect = "";
        internal int Gates, Profiles;
        internal Action? DuringProfile;
        internal ModuleDiaryEntry Entry()
        {
            var entry = new ModuleDiaryEntry(Owner, "learning", new(2024, 2, 29));
            entry.SetReflection("private title", "private body"); entry.AddPlan(new(Guid.NewGuid(), "private plan")); return entry;
        }
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
    }

    private static Task Operation(IsolatedDiaryEditingService service, ModuleDiaryEntry entry, int operation, CancellationToken ct = default) => operation switch
    {
        0 => service.EditReflectionAsync(entry, "changed", "changed", ct),
        1 => service.RemoveReflectionAsync(entry, ct),
        2 => service.EditPlanAsync(entry, entry.Plans[0].Id, "changed", cancellationToken: ct),
        _ => service.RemovePlanAsync(entry, entry.Plans[0].Id, ct)
    };

    [Theory]
    [InlineData("foreign-owner")] [InlineData("foreign-module")] [InlineData("empty-owner")] [InlineData("revoked")]
    public async Task ForeignScopeOrRevocationDeniesEveryMutationWithoutChangingRecords(string defect)
    {
        for (var operation = 0; operation < 4; operation++)
        {
            var c = new Context { Defect = defect }; var entry = c.Entry(); var reflection = entry.Reflection; var plans = entry.Plans;
            var error = await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Operation(new(c, c), entry, operation));
            Assert.Same(reflection, entry.Reflection); Assert.Same(plans, entry.Plans); Assert.Equal(new DateOnly(2024, 2, 29), entry.Date);
            Assert.DoesNotContain("private body", error.ToString()); Assert.DoesNotContain(c.Owner.ToString(), error.ToString());
        }
    }

    [Fact]
    public async Task DeactivationDeniesOrdinaryOperationsAndPreservesRecordsForLaterAuthorizedAccess()
    {
        var c = new Context { Active = false }; var entry = c.Entry(); var reflection = entry.Reflection; var plans = entry.Plans;
        var s = new IsolatedDiaryEditingService(c, c);
        for (var operation = 0; operation < 4; operation++)
            await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => Operation(s, entry, operation));
        Assert.Equal(0, c.Profiles); Assert.Same(reflection, entry.Reflection); Assert.Same(plans, entry.Plans);
        c.Active = true; await s.EditReflectionAsync(entry, null, "authorized new body");
        Assert.Equal("private body", reflection!.Body); Assert.Same(plans, entry.Plans); Assert.Equal("learning", entry.Module);
        Assert.Equal(new DateOnly(2024, 2, 29), entry.Date);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeWorkOrDuringProfileResolutionPropagatesWithoutMutation(bool duringProfile)
    {
        for (var operation = 0; operation < 4; operation++)
        {
            var c = new Context(); var entry = c.Entry(); var reflection = entry.Reflection; var plans = entry.Plans;
            using var cts = new CancellationTokenSource();
            if (duringProfile) c.DuringProfile = cts.Cancel; else cts.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operation(new(c, c), entry, operation, cts.Token));
            Assert.Equal(cts.Token, error.CancellationToken); Assert.Same(reflection, entry.Reflection); Assert.Same(plans, entry.Plans);
        }
    }

    [Fact]
    public async Task ProductionModuleGateAndUnavailableDependenciesFailClosedEvenWithCompletionEvidence()
    {
        var c = new Context(); var entry = c.Entry(); var reflection = entry.Reflection; var plans = entry.Plans;
        var service = new IsolatedDiaryEditingService(new PrivateModuleAccess(c), c);
        for (var operation = 0; operation < 4; operation++)
            await Assert.ThrowsAsync<ProfileContractPendingException>(() => Operation(service, entry, operation));
        Assert.Equal(0, c.Profiles); Assert.Same(reflection, entry.Reflection); Assert.Same(plans, entry.Plans);
        c.Defect = "unavailable";
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => new IsolatedDiaryEditingService(c, c).RemoveReflectionAsync(entry));
        Assert.Same(reflection, entry.Reflection); Assert.Same(plans, entry.Plans);
    }
}
