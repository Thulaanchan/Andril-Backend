using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Common.DTOs;
using Andril.Common.Interfaces;
using Andril.IntegrationTests.Database;
using Andril.Persistence.Accounts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Lifecycle;

[Collection("ControlledLifecyclePostgreSQL")]
public sealed class ControlledProfileCoordinationTests
{
    [LocalPostgresFact]
    public async Task ActiveSetupIsUniqueAndInactiveAndForeignProfilesAreDenied()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var f = new Fixture(db, singleConnection: true);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Profiles.CreateAsync("learning"));
        await f.Activate();
        var setup = await Task.WhenAll(f.Profiles.CreateAsync("learning"), f.Profiles.CreateAsync("learning"));
        Assert.Equal(setup[0].ProfileId, setup[1].ProfileId);
        Assert.False(setup[0].IsComplete);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Profiles.CreateAsync("hobbies"));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.AsForeign().ReadAsync("learning", RejectRelease));
        await f.Stop();
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Profiles.ReadAsync("learning", RejectRelease));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Profiles.EditAsync("learning", new("Hidden", null, "private")));
        await using var owner = db.Open();
        Assert.Equal(setup[0].ProfileId, (await owner.PrivateProfiles.SingleAsync()).Id);
    }

    [LocalPostgresFact]
    public async Task EditBeforeDeactivationCommitsAndLaterEditCannotUseEarlierAuthorization()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var f = new Fixture(db, singleConnection: true);
        await f.Activate();
        await f.Profiles.CreateAsync("learning");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var gate = f.Authority.Pause(PrivateProfileOperation.Edit, secondCheck: true);
        var edit = f.Profiles.EditAsync("learning", new("Before revoke", null, "private"), deadline.Token);
        await gate.Entered.Task.WaitAsync(deadline.Token);
        var stop = f.Stop(deadline.Token);
        try { await f.ObserveWaitingLockAsync(deadline.Token); }
        finally { gate.Release.TrySetResult(); }

        Assert.Equal("Before revoke", (await edit).DisplayName);
        Assert.Equal("inactive", (await stop).State);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Profiles.EditAsync("learning", new("After revoke", null, "private")));
        await using var owner = db.Open();
        Assert.Equal("Before revoke", (await owner.PrivateProfiles.SingleAsync()).DisplayName);
    }

    [LocalPostgresFact]
    public async Task ReadReleaseRunsUnderFenceAndAfterDeactivationDisclosesNothing()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var f = new Fixture(db, singleConnection: true);
        await f.Activate();
        var profile = await f.Profiles.CreateAsync("learning");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var release = new Barrier();
        Guid? delivered = null;
        var read = f.Profiles.ReadAsync("learning", async (response, ct) =>
        {
            release.Entered.TrySetResult();
            await release.Release.Task.WaitAsync(ct);
            delivered = response.ProfileId;
        }, deadline.Token);
        await release.Entered.Task.WaitAsync(deadline.Token);
        var stop = f.Stop(deadline.Token);
        try
        {
            await f.ObserveWaitingLockAsync(deadline.Token);
            Assert.Null(delivered);
        }
        finally { release.Release.TrySetResult(); }

        await read;
        Assert.Equal(profile.ProfileId, delivered);
        await stop;
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Profiles.ReadAsync("learning", RejectRelease));
    }

    [LocalPostgresFact]
    public async Task DelayedPhotoCannotFinalizeWhenDeactivationHoldsTheFenceFirst()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var f = new Fixture(db, gateDeactivation: true);
        await f.Activate();
        await f.Profiles.CreateAsync("learning");
        var (upload, lease) = await f.Reserve();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stop = f.Stop(deadline.Token);
        var gate = f.DeactivationGate!;
        await gate.Entered.Task.WaitAsync(deadline.Token);
        var finalize = f.Profiles.FinalizePhotoAsync("learning", upload, lease, deadline.Token);
        try { await f.ObserveWaitingLockAsync(deadline.Token); }
        finally { gate.Release.TrySetResult(); }

        await stop;
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => finalize);
        Assert.Equal(0, f.Objects.Reads);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Profiles.ReadPhotoMetadataAsync("learning", upload,
            (_, _) => throw new InvalidOperationException("Inactive metadata was released.")));
        await using var owner = db.Open();
        var retained = await owner.PrivateProfilePhotos.SingleAsync();
        Assert.Equal(PhotoState.PendingStorage, retained.State);
        Assert.False(retained.IsCurrent);
    }

    [LocalPostgresFact]
    public async Task PhotoFinalizationVerifiesLeaseIntegrityAndFreshPermissionBeforeCommit()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var f = new Fixture(db, singleConnection: true);
        await f.Activate();
        await f.Profiles.CreateAsync("learning");
        var (upload, lease) = await f.Reserve();
        await Assert.ThrowsAsync<PhotoUploadConflictException>(() => f.Profiles.FinalizePhotoAsync("learning", upload, Guid.NewGuid()));
        f.Objects.Corrupt = true;
        await Assert.ThrowsAsync<PhotoDependencyException>(() => f.Profiles.FinalizePhotoAsync("learning", upload, lease));
        f.Objects.Corrupt = false;
        using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var gate = f.Authority.Pause(PrivateProfileOperation.FinalizeMedia, secondCheck: true);
        var pending = f.Profiles.FinalizePhotoAsync("learning", upload, lease, canceled.Token);
        await gate.Entered.Task.WaitAsync(canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        f.Authority.Unavailable = true;
        await Assert.ThrowsAsync<Andril.Common.Accounts.AccountStoreUnavailableException>(() =>
            f.Profiles.FinalizePhotoAsync("learning", upload, lease));
        f.Authority.Unavailable = false;

        await using (var owner = db.Open())
            Assert.Equal(PhotoState.PendingStorage, (await owner.PrivateProfilePhotos.SingleAsync()).State);
        Assert.Equal(PhotoState.Stored, (await f.Profiles.FinalizePhotoAsync("learning", upload, lease)).State);
        await f.Profiles.ReadPhotoContentAsync("learning", (bytes, _) =>
        {
            Assert.Equal(f.Objects.Bytes, bytes);
            return Task.CompletedTask;
        });
    }

    [LocalPostgresFact]
    public async Task ControlledRoleCannotReadUnscopedProfileOrChangeOwnershipOnPoolReuse()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var f = new Fixture(db, singleConnection: true);
        await f.Activate();
        await f.Profiles.CreateAsync("learning");
        await using var runtime = new NpgsqlConnection(f.Connection);
        await runtime.OpenAsync();
        Assert.Equal(0L, await new NpgsqlCommand("SELECT count(*) FROM andril_identity.private_module_profiles", runtime).ExecuteScalarAsync());
        await using (var tx = await runtime.BeginTransactionAsync())
        {
            await using var scope = new NpgsqlCommand("""
                SELECT set_config('andril.account', @account, true), set_config('andril.module', 'learning', true)
                """, runtime, tx);
            scope.Parameters.AddWithValue("account", db.AccountId.ToString("D"));
            await scope.ExecuteNonQueryAsync();
            await using var unverified = new NpgsqlCommand("SELECT count(*) FROM andril_identity.private_module_profiles", runtime, tx);
            Assert.Equal(0L, await unverified.ExecuteScalarAsync());
            await tx.RollbackAsync();
        }
        await using var denied = new NpgsqlCommand("UPDATE andril_identity.private_module_profiles SET account_id = account_id", runtime);
        var error = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private static Task RejectRelease(PrivateProfileDto _, CancellationToken __) =>
        throw new InvalidOperationException("A denied protected profile was released.");

    private sealed class Fixture
    {
        private readonly ControlledLifecycleDatabase db;
        private readonly ControlledModuleLifecycleStore store;
        internal string Connection { get; }
        internal PurposeAuthority Authority { get; } = new();
        internal ObjectsStub Objects { get; } = new();
        internal Barrier? DeactivationGate { get; }
        internal ControlledModuleProfileOperations Profiles { get; }

        internal Fixture(ControlledLifecycleDatabase db, bool gateDeactivation = false, bool singleConnection = false)
        {
            this.db = db;
            Connection = new NpgsqlConnectionStringBuilder(db.RuntimeConnection) { MaxPoolSize = singleConnection ? 1 : 4 }.ConnectionString;
            DeactivationGate = gateDeactivation ? new Barrier() : null;
            store = new(Connection, db.Authority, db.Authority, db.Authority, true,
                DeactivationGate is { } gate ? new GatedDeactivation(db.DeactivationAuthority, gate) : db.DeactivationAuthority);
            Profiles = As(db.Subject);
        }

        internal ControlledModuleProfileOperations AsForeign() => As(Guid.NewGuid());

        private ControlledModuleProfileOperations As(Guid subject)
        {
            var actor = new Actor(subject);
            return new(store, actor, new(actor, actor, TimeProvider.System), Authority, Objects);
        }

        internal Task<ModuleLifecycleResult> Activate() =>
            db.Service(store).ExecuteAsync(db.Subject, new("learning", Guid.NewGuid(), false), Guid.NewGuid());

        internal Task<ModuleLifecycleResult> Stop(CancellationToken ct = default) =>
            db.Service(store).ExecuteAsync(db.Subject,
                new("learning", Guid.NewGuid(), false, 1, ModuleLifecycleOperation.Deactivate, new(db.AccountId, "learning")),
                Guid.NewGuid(), ct);

        internal async Task<(Guid Upload, Guid Lease)> Reserve()
        {
            var hash = PhotoHashes.Compute(Objects.Bytes);
            (Guid Upload, Guid Lease) result = default;
            await Profiles.ReservePhotoAsync("learning", Guid.NewGuid(),
                new(hash, hash, "image/jpeg", Objects.Bytes.Length, 320, 320), (id, lease, _) =>
                {
                    result = (id, lease);
                    return Task.CompletedTask;
                });
            Assert.NotEqual(Guid.Empty, result.Upload);
            Assert.NotEqual(Guid.Empty, result.Lease);
            return result;
        }

        internal async Task ObserveWaitingLockAsync(CancellationToken ct)
        {
            await using var observer = new NpgsqlConnection(Connection);
            await observer.OpenAsync(ct);
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted
                    AND database = (SELECT oid FROM pg_database WHERE datname = current_database()))
                """, observer);
            while ((bool)(await command.ExecuteScalarAsync(ct))! is false)
                await Task.Delay(10, ct);
        }
    }

    private sealed class Actor(Guid subject) : ICurrentUser, IOnboardingIdentity
    {
        public Guid? UserId => subject;
        public bool IsAuthenticated => true;
        public bool IsConfirmedFor(Guid candidate) => candidate == subject;
    }

    private sealed class Barrier
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PurposeAuthority : IControlledProfileAuthority
    {
        private PrivateProfileOperation? operationToPause;
        private int requestedCheck;
        private int checks;
        private Barrier? barrier;
        internal bool Unavailable { get; set; }

        internal Barrier Pause(PrivateProfileOperation operation, bool secondCheck)
        {
            operationToPause = operation;
            requestedCheck = secondCheck ? 2 : 1;
            checks = 0;
            return barrier = new();
        }

        public async Task<ControlledProfilePermission> ReadAsync(Guid subject, ModuleMembershipScope scope, Guid? profileId,
            PrivateProfileOperation operation, long revision, Guid eligibilityVersion, CancellationToken ct)
        {
            if (operation == operationToPause && Interlocked.Increment(ref checks) == requestedCheck && barrier is { } gate)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(ct);
            }
            var outcome = Unavailable ? ModuleAuthorizationOutcome.Unavailable : ModuleAuthorizationOutcome.Satisfied;
            return new(true, new(scope, profileId, operation, revision, eligibilityVersion, outcome), DateTimeOffset.UtcNow.AddMinutes(1));
        }
    }

    private sealed class GatedDeactivation(IControlledDeactivationAuthority inner, Barrier gate) : IControlledDeactivationAuthority
    {
        public async Task<ModuleDeactivationEvidence> ReadAsync(Guid subject, ModuleMembershipScope scope,
            DateTimeOffset databaseTime, CancellationToken cancellationToken)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
            return await inner.ReadAsync(subject, scope, databaseTime, cancellationToken);
        }
    }

    private sealed class ObjectsStub : IPrivateProfilePhotoObjectStorage
    {
        public bool IsConfigured => true;
        internal byte[] Bytes { get; } = [1, 3, 5, 7];
        internal bool Corrupt { get; set; }
        internal int Reads { get; private set; }
        public Task<PhotoObjectOutcome> UploadAsync(StoredProfilePhoto expected, byte[] jpeg, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No external storage is used.");
        public Task<PhotoObjectRead> ReadAsync(StoredProfilePhoto expected, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new PhotoObjectRead(PhotoObjectOutcome.Success, Corrupt ? [0, 0, 0, 0] : Bytes));
        }
        public Task<PhotoObjectOutcome> DeleteAsync(PhotoObjectAddress address, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No external storage is used.");
    }
}
