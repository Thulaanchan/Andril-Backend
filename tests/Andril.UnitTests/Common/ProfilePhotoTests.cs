using Andril.Common.Accounts.Profiles;
using Andril.Common.DTOs;
using Andril.Common.Entities;
using Andril.Common.Interfaces;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ProfilePhotoTests
{
    [Theory]
    [InlineData("  ALIce  ", "alice")]
    [InlineData("\u00a0ABC\u2000", "abc")]
    [InlineData("ÄA", "Äa")]
    public void NamesUseTrimAndAsciiOnly(string value, string expected) => Assert.Equal(expected, DisplayNames.Canonical(value));

    [Fact]
    public async Task ReplacementKeepsPreviousCurrentUntilCommitAndNeverReactivatesOldRequests()
    {
        var f = new Fixture();
        var firstRequest = Guid.NewGuid();
        var first = await f.Upload(firstRequest, 1);
        Assert.True(first.IsCurrent);
        Assert.NotEqual(firstRequest, first.UploadId);
        Assert.Equal(first, await f.Upload(firstRequest, 1));
        Assert.Equal(1, f.Inspections);
        await Assert.ThrowsAsync<PhotoUploadConflictException>(() => f.Upload(firstRequest, 2));
        f.BeforeWrite = async () => Assert.Equal(first, await f.Service.GetCurrentAsync("social"));
        f.DeleteOutcome = PhotoObjectOutcome.TransientFailure;
        var second = await f.Upload(Guid.NewGuid(), 2);
        Assert.True(second.IsCurrent);
        Assert.True(f.Rows[first.UploadId].CleanupPending);
        Assert.Equal(new byte[] { 42, 2 }, await f.Service.GetContentAsync("social", second.UploadId));
        var historical = await f.Upload(firstRequest, 1);
        Assert.Equal(PhotoState.Stored, historical.State);
        Assert.False(historical.IsCurrent);
        await Assert.ThrowsAsync<ProfileMissingException>(() => f.Service.GetContentAsync("social", first.UploadId));
    }

    [Fact]
    public async Task AmbiguousWriteResumesUsingPersistedEvidenceWithoutDecodingAgain()
    {
        var f = new Fixture { WriteOutcome = PhotoObjectOutcome.Ambiguous };
        var request = Guid.NewGuid();
        await Assert.ThrowsAsync<PhotoDependencyException>(() => f.Upload(request, 1));
        var pending = Assert.Single(f.Rows.Values);
        Assert.Equal(PhotoState.PendingStorage, pending.State);
        await Assert.ThrowsAsync<ProfileMissingException>(() => f.Service.GetContentAsync("social", pending.Id));
        f.Now += TimeSpan.FromMinutes(3);
        var resumed = await f.Upload(request, 1);
        Assert.Equal(pending.Id, resumed.UploadId);
        Assert.True(resumed.IsCurrent);
        Assert.Equal(1, f.Writes);
        Assert.Equal(1, f.Inspections);
    }

    [Fact]
    public async Task InterruptedFinalizationCanResumeWithoutOverwritingStorage()
    {
        var f = new Fixture { DenyFinalization = true };
        var request = Guid.NewGuid();
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Upload(request, 7));
        var pending = Assert.Single(f.Rows.Values);
        Assert.False(pending.IsCurrent);
        f.DenyFinalization = false;
        f.Now += TimeSpan.FromMinutes(3);
        Assert.True((await f.Upload(request, 7)).IsCurrent);
        Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task FailedOrMismatchedReplacementRetainsPreviousCurrentAndAllowsNewAttempt()
    {
        var f = new Fixture();
        var first = await f.Upload(Guid.NewGuid(), 1);
        f.WriteOutcome = PhotoObjectOutcome.PermanentFailure;
        var failed = await f.Upload(Guid.NewGuid(), 2);
        Assert.Equal(PhotoState.Failed, failed.State);
        await Assert.ThrowsAsync<ProfileMissingException>(() => f.Service.GetContentAsync("social", failed.UploadId));
        f.WriteOutcome = PhotoObjectOutcome.Success;
        f.Corrupt = true;
        var mismatch = await f.Upload(Guid.NewGuid(), 3);
        Assert.Equal(PhotoReason.IntegrityFailure, mismatch.Reason);
        Assert.Equal(first, await f.Service.GetCurrentAsync("social"));
        f.Corrupt = false;
        Assert.True((await f.Upload(Guid.NewGuid(), 4)).IsCurrent);
    }

    [Fact]
    public async Task BoundariesRejectEmptyOversizedDisabledAndForeignRequests()
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => f.Upload(Guid.NewGuid()));
        await Assert.ThrowsAsync<PhotoUploadTooLargeException>(() => f.Upload(Guid.NewGuid(), new byte[17]));
        Assert.Empty(f.Rows);
        f.Ready = false;
        await Assert.ThrowsAsync<PhotoDependencyException>(() => f.Upload(Guid.NewGuid(), 1));
        Assert.Equal(0, f.Inspections);
        Assert.Equal(0, f.Writes);
        f.Ready = true;
        var photo = await f.Upload(Guid.NewGuid(), 1);
        await Assert.ThrowsAsync<ProfileMissingException>(() => f.Service.GetAsync("hobbies", photo.UploadId));
        await Assert.ThrowsAsync<ProfileMissingException>(() => f.Service.GetContentAsync("hobbies", photo.UploadId));
        f.Owner = Guid.NewGuid();
        await Assert.ThrowsAsync<ProfileMissingException>(() => f.Service.GetContentAsync("social", photo.UploadId));
    }

    [Fact]
    public async Task InspectorRejectionLeavesCurrentPhotoAndAllStorageStateUnchanged()
    {
        var f = new Fixture();
        var existing = await f.Upload(Guid.NewGuid(), 1);
        var prior = (f.Writes, f.Deletes, f.StorageReads, f.Rows.Count);
        f.InspectorRejects = true;
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => f.Upload(Guid.NewGuid(), 2));
        Assert.Equal(prior, (f.Writes, f.Deletes, f.StorageReads, f.Rows.Count));
        Assert.Equal(existing, await f.Service.GetCurrentAsync("social"));
    }

    [Theory]
    [InlineData("image/png", 300, 300, 1)]
    [InlineData("image/jpeg", 0, 300, 1)]
    [InlineData("image/jpeg", 300, 1025, 1)]
    [InlineData("image/jpeg", 300, 300, 0)]
    [InlineData("image/jpeg", 300, 300, 17)]
    public async Task InvalidSanitizedEvidenceCannotReserveOrWrite(string type, int width, int height, int length)
    {
        var f = new Fixture { InspectorEvidence = new(new byte[length], type, width, height) };
        await Assert.ThrowsAsync<PhotoUploadInvalidException>(() => f.Upload(Guid.NewGuid(), 1));
        Assert.Empty(f.Rows);
        Assert.Equal((0, 0, 0), (f.Writes, f.Deletes, f.StorageReads));
    }

    [Fact]
    public async Task AuthorizationLostAfterInspectionCannotReserveWriteOrCleanup()
    {
        var f = new Fixture { DenyAtRead = 2 };
        var previous = f.QueueFailedCleanup();
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Upload(Guid.NewGuid(), 1));
        Assert.Equal(1, f.Inspections);
        Assert.Same(previous, Assert.Single(f.Rows.Values));
        Assert.True(previous.CleanupPending);
        Assert.Null(previous.CleanupClaimId);
        Assert.Equal((0, 0, 0), (f.Writes, f.Deletes, f.StorageReads));
    }

    [Fact]
    public async Task SharedCleanupDeadlineIncludesPriorAttemptsAndFencesUnrecordedTimeout()
    {
        var clock = new CleanupTestClock();
        var f = new Fixture { CleanupClock = clock };
        var candidates = Enumerable.Range(0, 4).Select(_ => f.QueueFailedCleanup()).ToArray();
        f.Deletion = token =>
        {
            clock.Advance(TimeSpan.FromSeconds(f.Deletes == 1 ? 3 : 2));
            token.ThrowIfCancellationRequested();
            return Task.FromResult(PhotoObjectOutcome.TransientFailure);
        };

        var published = await f.Upload(Guid.NewGuid(), 1);
        Assert.Equal(published, await f.Service.GetCurrentAsync("social"));
        Assert.Equal(2, f.Deletes);
        Assert.All(candidates, row => Assert.True(row.CleanupPending));
        Assert.Single(candidates, row => row.CleanupClaimId.HasValue);
        Assert.Single(candidates, row => row.NextCleanupAttemptAtUtc == f.Now.AddSeconds(60));
        Assert.Equal(2, candidates.Count(row => row.CleanupClaimId is null && row.NextCleanupAttemptAtUtc == f.Now));
    }

    [Fact]
    public async Task ExhaustingBudgetInClaimCoordinationDoesNotStartADelete()
    {
        var clock = new CleanupTestClock();
        var f = new Fixture { CleanupClock = clock };
        var candidate = f.QueueFailedCleanup();
        f.AfterClaim = () => clock.Advance(TimeSpan.FromSeconds(5));

        var result = await f.Upload(Guid.NewGuid(), 1);
        Assert.True(result.IsCurrent);
        Assert.Equal(0, f.Deletes);
        Assert.True(candidate.CleanupPending);
        Assert.NotNull(candidate.CleanupClaimId);
    }

    [Fact]
    public async Task CleanupAttemptsAtMostFourAndHonorsRetryDelayAcrossRequests()
    {
        var f = new Fixture { DeleteOutcome = PhotoObjectOutcome.TransientFailure };
        var candidates = Enumerable.Range(0, 5).Select(_ => f.QueueFailedCleanup()).ToArray();
        var request = Guid.NewGuid();
        var first = await f.Upload(request, 1);
        Assert.Equal(4, f.Deletes);
        Assert.Equal(4, candidates.Count(row => row.NextCleanupAttemptAtUtc == f.Now.AddSeconds(60)));
        Assert.Equal(first, await f.Upload(request, 1));
        Assert.Equal(5, f.Deletes);
        f.Now += TimeSpan.FromSeconds(59);
        await f.Upload(request, 1);
        Assert.Equal(5, f.Deletes);
        f.Now += TimeSpan.FromSeconds(1);
        await f.Upload(request, 1);
        Assert.Equal(9, f.Deletes);
    }

    [Fact]
    public async Task UnexpectedCleanupFailureDoesNotMaskCommittedReplacement()
    {
        var f = new Fixture();
        var old = await f.Upload(Guid.NewGuid(), 1);
        f.Deletion = _ => throw new InvalidOperationException("Synthetic cleanup failure");

        var published = await f.Upload(Guid.NewGuid(), 2);
        Assert.Equal(published, await f.Service.GetCurrentAsync("social"));
        Assert.True(f.Rows[old.UploadId].CleanupPending);
        Assert.NotNull(f.Rows[old.UploadId].CleanupClaimId);
    }

    [Fact]
    public async Task ClientCancellationDuringCleanupPreservesCommittedReplacement()
    {
        var f = new Fixture();
        var old = await f.Upload(Guid.NewGuid(), 1);
        using var client = new CancellationTokenSource();
        f.Deletion = token =>
        {
            client.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected a linked cancellation token.");
        };

        var replacement = await f.Service.UploadAsync("social", Guid.NewGuid(), new MemoryStream([2]), client.Token);
        Assert.Equal(replacement, await f.Service.GetCurrentAsync("social"));
        Assert.True(f.Rows[old.UploadId].CleanupPending);
        Assert.NotNull(f.Rows[old.UploadId].CleanupClaimId);
    }

    [Fact]
    public async Task CleanupIgnoresTerminalRowsOutsideAuthorizedProfileOrModule()
    {
        var f = new Fixture();
        var foreignProfile = f.QueueFailedCleanup();
        f.Owner = Guid.NewGuid();
        var foreignModule = f.QueueFailedCleanup("learning");
        var result = await f.Upload(Guid.NewGuid(), 1);
        Assert.True(result.IsCurrent);
        Assert.Equal(0, f.Deletes);
        Assert.All(new[] { foreignProfile, foreignModule }, row =>
        {
            Assert.True(row.CleanupPending);
            Assert.Null(row.CleanupClaimId);
        });
    }

    private sealed class CleanupTestClock : TimeProvider
    {
        private readonly List<TestTimer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            ticks += elapsed.Ticks;
            foreach (var timer in timers.ToArray()) timer.FireIfDue(ticks);
        }

        private sealed class TestTimer(CleanupTestClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long deadline = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                deadline = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks;
                return true;
            }

            internal void FireIfDue(long now)
            {
                if (deadline > now) return;
                deadline = long.MaxValue;
                callback(state);
            }

            public void Dispose() => clock.timers.Remove(this);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Fixture : IPrivateProfileService, IPhotoUploadPolicy, IProfileImageInspector,
        IPrivateProfilePhotoStore, IPrivateProfilePhotoObjectStorage
    {
        internal Guid Owner = Guid.NewGuid();
        internal DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        internal bool Ready = true, Corrupt, DenyFinalization, InspectorRejects;
        internal SafeProfileImage? InspectorEvidence;
        internal int Inspections, Writes, Deletes, StorageReads, ScopeReads, DenyAtRead;
        internal Func<CancellationToken, Task<PhotoObjectOutcome>>? Deletion;
        internal TimeProvider? CleanupClock;
        internal Action? AfterClaim;
        internal PhotoObjectOutcome WriteOutcome = PhotoObjectOutcome.Success, DeleteOutcome = PhotoObjectOutcome.Success;
        internal Func<Task>? BeforeWrite;
        internal readonly Dictionary<Guid, PrivateProfilePhoto> Rows = [];
        private readonly Dictionary<PhotoObjectAddress, byte[]> objects = [];
        internal ProfilePhotoService Service => new(this, this, this, this, this, CleanupClock);
        internal Task<PrivatePhotoStatus> Upload(Guid request, params byte[] bytes) => Service.UploadAsync("social", request, new MemoryStream(bytes));

        internal PrivateProfilePhoto QueueFailedCleanup(string module = "social")
        {
            var scope = new PhotoScope(Owner, module);
            var hash = new string('a', 64);
            var evidence = new PhotoUploadEvidence(hash, hash, "image/jpeg", 1, 300, 300);
            var token = Guid.NewGuid();
            var photo = PrivateProfilePhoto.Create(scope, Guid.NewGuid(), Guid.NewGuid(), evidence, token, Now, Now.AddMinutes(2));
            photo.MarkFailed(token, PhotoReason.StorageFailure, Now);
            Rows.Add(photo.Id, photo);
            return photo;
        }

        public bool IsApprovedAndConfigured => Ready;
        public bool IsConfigured => Ready;
        public int RequireMaximumBytes() => 16;
        public Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken ct)
        {
            Inspections++;
            if (InspectorRejects) throw new PhotoUploadInvalidException();
            return Task.FromResult(InspectorEvidence ?? new SafeProfileImage([42, .. bytes], "image/jpeg", 300, 300));
        }

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken ct = default)
        {
            if (++ScopeReads == DenyAtRead) throw new ProfileAccessDeniedException();
            return Task.FromResult(new PrivateProfileDto(Owner, module, Now));
        }
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken ct) =>
            DenyFinalization ? throw new ProfileAccessDeniedException() : await action(await GetAsync(module, ct));

        public Task<PrivateProfilePhoto?> FindRequestAsync(PhotoScope scope, Guid requestId, CancellationToken ct) =>
            Task.FromResult(Scoped(scope).SingleOrDefault(x => x.RequestId == requestId));
        public async Task<PrivateProfilePhoto> ReserveAsync(PhotoScope scope, Guid requestId, string originalHash, PhotoUploadEvidence? evidence, CancellationToken ct)
        {
            var old = await FindRequestAsync(scope, requestId, ct);
            if (old is not null)
            {
                if (old.State == PhotoState.PendingStorage) old.Reclaim(Guid.NewGuid(), Now, Now.AddMinutes(2));
                return old;
            }
            Scoped(scope).SingleOrDefault(x => x.State == PhotoState.PendingStorage)?.Abandon(Now);
            var row = PrivateProfilePhoto.Create(scope, Guid.NewGuid(), requestId, evidence!, Guid.NewGuid(), Now, Now.AddMinutes(2));
            Rows.Add(row.Id, row);
            return row;
        }

        public Task<bool> OwnsLeaseAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct) =>
            Task.FromResult(Scoped(scope).Single(x => x.Id == id).OwnsLease(token, Now));
        public Task<PrivatePhotoStatus> FinalizeAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct)
        {
            Scoped(scope).SingleOrDefault(x => x.IsCurrent)?.MarkSuperseded(Now);
            var row = Scoped(scope).Single(x => x.Id == id);
            row.MarkStored(token, Now);
            return Task.FromResult(row.Status());
        }
        public Task<PrivatePhotoStatus> FailAsync(PhotoScope scope, Guid id, Guid token, PhotoReason reason, CancellationToken ct)
        {
            var row = Scoped(scope).Single(x => x.Id == id);
            row.MarkFailed(token, reason, Now);
            return Task.FromResult(row.Status());
        }

        public Task<PrivatePhotoStatus?> ReadAsync(PhotoScope scope, Guid id, CancellationToken ct) =>
            Task.FromResult(Scoped(scope).SingleOrDefault(x => x.Id == id)?.Status());
        public Task<StoredProfilePhoto?> ReadCurrentAsync(PhotoScope scope, CancellationToken ct) =>
            Task.FromResult(Scoped(scope).SingleOrDefault(x => x.IsCurrent)?.Evidence());
        public Task<PhotoCleanupClaim?> ClaimCleanupAsync(PhotoScope scope, CancellationToken ct)
        {
            var claim = Scoped(scope).Where(x => x.CanClaimCleanup(Now)).OrderBy(x => x.NextCleanupAttemptAtUtc)
                .FirstOrDefault()?.ClaimCleanup(Guid.NewGuid(), Now, Now.AddSeconds(65));
            if (claim is not null) AfterClaim?.Invoke();
            return Task.FromResult(claim);
        }

        public Task<bool> RecordCleanupAsync(PhotoCleanupClaim claim, bool removed, TimeSpan retryDelay, CancellationToken ct) =>
            Task.FromResult(Scoped(claim.Address.Scope).Single(x => x.Id == claim.Address.UploadId)
                .RecordCleanupAttempt(claim.Token, removed, retryDelay, Now));
        public async Task<PhotoObjectOutcome> UploadAsync(StoredProfilePhoto expected, byte[] jpeg, CancellationToken ct)
        {
            Writes++;
            if (BeforeWrite is not null) await BeforeWrite();
            if (WriteOutcome != PhotoObjectOutcome.PermanentFailure)
                objects.Add(expected.Address, Corrupt ? [99, 99] : jpeg);
            return WriteOutcome;
        }
        public Task<PhotoObjectRead> ReadAsync(StoredProfilePhoto expected, CancellationToken ct)
        {
            StorageReads++;
            return Task.FromResult(objects.TryGetValue(expected.Address, out var bytes)
                ? new PhotoObjectRead(PhotoObjectOutcome.Success, bytes) : new(PhotoObjectOutcome.Missing));
        }

        public async Task<PhotoObjectOutcome> DeleteAsync(PhotoObjectAddress address, CancellationToken ct)
        {
            Deletes++;
            var outcome = Deletion is null ? DeleteOutcome : await Deletion(ct);
            if (outcome == PhotoObjectOutcome.Success) objects.Remove(address);
            return outcome;
        }

        private IEnumerable<PrivateProfilePhoto> Scoped(PhotoScope scope) =>
            Rows.Values.Where(x => x.ProfileId == scope.ProfileId && x.Module == scope.Module);
    }
}
