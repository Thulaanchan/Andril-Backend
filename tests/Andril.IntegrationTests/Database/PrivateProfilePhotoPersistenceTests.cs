using System.Collections.Concurrent;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.DTOs;
using Andril.Common.Interfaces;
using Andril.Shared.Authentication;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class PrivateProfilePhotoPersistenceTests
{
    private const string Migration = "20261120152336_AddPrivateProfilePhotos";
    private static readonly string Hash = new('a', 64);
    private static readonly PhotoUploadEvidence Evidence = new(Hash, new string('b', 64), "image/jpeg", 29, 300, 300);

    [Fact]
    public void MigrationIsAdditiveScopedAndMatchesSnapshot()
    {
        using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql("Host=localhost;Database=not_connected;Username=not_connected").Options);
        Assert.Contains(Migration, db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        var sql = migrator.GenerateScript("20261116000200_AddPrivateProfileFields", Migration);
        Assert.Contains("CREATE TABLE andril_identity.private_profile_photos", sql);
        Assert.Contains("REFERENCES andril_identity.private_module_profiles (id, module)", sql);
        Assert.Contains("WHERE is_current", sql);
        Assert.Contains("WHERE state = 'PendingStorage'", sql);
        foreach (var expected in new[] { "uq_profile_photo_request", "uq_profile_photo_object", "ck_profile_photo_state",
            "ck_profile_photo_hashes", "ck_profile_photo_key", "ck_profile_photo_times", "ck_profile_photo_cleanup",
            "cleanup_claim_id", "cleanup_claim_expires_at_utc", "next_cleanup_attempt_at_utc", "ix_profile_photo_cleanup",
            "ENABLE ROW LEVEL SECURITY",
            "FROM PUBLIC", "FROM anon", "FROM authenticated" })
            Assert.Contains(expected, sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("DROP INDEX", sql);
        Assert.DoesNotContain("GRANT ", sql);
        var down = migrator.GenerateScript(Migration, "20261116000200_AddPrivateProfileFields");
        Assert.Contains("DROP TABLE andril_identity.private_profile_photos", down);
        Assert.DoesNotContain("DROP TABLE andril_identity.private_module_profiles", down);
    }

    [LocalPostgresFact]
    public async Task ConcurrentReservationsUseOnePendingAndPersistDistinctModuleScopes()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, _) = await SeedAsync(local);
        async Task<bool> TryReserve()
        {
            await using var db = local.Open();
            try
            {
                await new PrivateProfilePhotoStore(db).ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
                return true;
            }
            catch (PhotoUploadConflictException) { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => TryReserve()));
        Assert.Single(results, value => value);
        await using var check = local.Open();
        var pending = Assert.Single(await check.PrivateProfilePhotos.ToListAsync());
        var reads = new PrivateProfilePhotoStore(check);
        Assert.NotNull(await reads.ReadAsync(scope, pending.Id, default));
        Assert.Null(await reads.ReadAsync(scope with { Module = "hobbies" }, pending.Id, default));
        Assert.Null(await reads.ReadAsync(scope with { ProfileId = Guid.NewGuid() }, pending.Id, default));
    }

    [LocalPostgresFact]
    public async Task SwitchRollsBackThenPublishesAndRetainsHistoryAcrossContexts()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        await using var db = local.Open();
        var store = new PrivateProfilePhotoStore(db);
        var profile = new PrivateProfileRepository(db);
        Task<PrivatePhotoStatus> Publish(Guid id, Guid token) =>
            profile.WithAuthorizedProfileAsync(account, scope.Module, _ => { },
                _ => store.FinalizeAsync(scope, id, token, default), default);
        var first = await store.ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
        await Publish(first.Id, first.LeaseToken!.Value);
        var second = await store.ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
        Guid token = second.LeaseToken!.Value;
        await Assert.ThrowsAsync<InvalidOperationException>(() => profile.WithAuthorizedProfileAsync<PrivatePhotoStatus>(
            account, scope.Module, _ => { }, async _ =>
            {
                await store.FinalizeAsync(scope, second.Id, token, default);
                throw new InvalidOperationException("Intentional rollback after both updates.");
            }, default));
        await using var restart = local.Open();
        var fresh = new PrivateProfilePhotoStore(restart);
        Assert.Equal(first.Id, (await fresh.ReadCurrentAsync(scope, default))!.Address.UploadId);
        Assert.Equal(PhotoState.PendingStorage, (await fresh.ReadAsync(scope, second.Id, default))!.State);

        var committed = await new PrivateProfileRepository(restart).WithAuthorizedProfileAsync(account, scope.Module, _ => { },
            _ => fresh.FinalizeAsync(scope, second.Id, token, default), default);
        Assert.True(committed.IsCurrent);
        var repeated = await new PrivateProfileRepository(restart).WithAuthorizedProfileAsync(account, scope.Module, _ => { },
            _ => fresh.FinalizeAsync(scope, second.Id, token, default), default);
        Assert.Equal(committed, repeated);
        Assert.Equal(second.Id, (await fresh.ReadCurrentAsync(scope, default))!.Address.UploadId);
        var historical = await fresh.ReserveAsync(scope, first.RequestId, Hash, null, default);
        Assert.False(historical.IsCurrent);
        Assert.True(historical.CleanupPending);
        Assert.Equal(first.Id, (await fresh.ClaimCleanupAsync(scope, default))!.Address.UploadId);
    }

    [LocalPostgresFact]
    public async Task AbandonedFenceProtectsCurrentAndRetainsCleanupEvidence()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        await using var db = local.Open();
        var store = new PrivateProfilePhotoStore(db);
        var first = await store.ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
        var oldToken = first.LeaseToken!.Value;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.private_profile_photos
            SET created_at_utc = clock_timestamp() - interval '5 minutes',
                updated_at_utc = clock_timestamp() - interval '5 minutes',
                lease_until_utc = clock_timestamp() - interval '2 minutes'
            WHERE id = {first.Id}
            """);
        db.ChangeTracker.Clear();
        var newPhoto = await store.ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
        var repository = new PrivateProfileRepository(db);
        await Assert.ThrowsAsync<PhotoUploadConflictException>(() => repository.WithAuthorizedProfileAsync(account, scope.Module,
            _ => { }, _ => store.FinalizeAsync(scope, first.Id, oldToken, default), default));
        var claim = Assert.IsType<PhotoCleanupClaim>(await store.ClaimCleanupAsync(scope, default));
        Assert.Equal(first.Id, claim.Address.UploadId);
        Assert.True(await store.RecordCleanupAsync(claim, true, PhotoCleanupSettings.Approved.RetryDelay, default));
        Assert.Null(await store.ClaimCleanupAsync(scope, default));
        Assert.True((await db.PrivateProfilePhotos.AsNoTracking().SingleAsync(x => x.Id == first.Id)).CleanupPending);
        await repository.WithAuthorizedProfileAsync(account, scope.Module, _ => { },
            _ => store.FinalizeAsync(scope, newPhoto.Id, newPhoto.LeaseToken!.Value, default), default);
        Assert.Equal(newPhoto.Id, (await store.ReadCurrentAsync(scope, default))!.Address.UploadId);
    }

    [LocalPostgresFact]
    public async Task DatabaseRejectsMismatchedScopeDuplicateRequestsAndMultipleCurrentOrPendingRows()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        await using var db = local.Open();
        var store = new PrivateProfilePhotoStore(db);
        Task<int> Insert(Guid owner, string module, Guid request, bool current, string state)
        {
            var id = Guid.NewGuid();
            var key = new PhotoObjectAddress(new(owner, module), id).Key;
            Guid? token = state == "PendingStorage" ? Guid.NewGuid() : null;
            DateTimeOffset? expires = token.HasValue ? DateTimeOffset.UtcNow.AddMinutes(2) : null;
            return db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.private_profile_photos
                    (id, profile_id, module, request_id, object_key, original_content_hash, stored_content_hash,
                     media_type, file_size_bytes, width, height, state, reason, is_current, cleanup_pending,
                     late_write_risk, lease_token, lease_until_utc, created_at_utc, updated_at_utc)
                VALUES ({id}, {owner}, {module}, {request}, {key}, {Hash}, {Evidence.StoredHash},
                        'image/jpeg', 20, 300, 300, {state}, 'None', {current}, false,
                        false, {token}, {expires}, clock_timestamp(), clock_timestamp())
                """);
        }

        // Probe scope and state before a reservation can introduce a competing uniqueness violation.
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await Assert.ThrowsAsync<PostgresException>(
            () => Insert(scope.ProfileId, "hobbies", Guid.NewGuid(), false, "PendingStorage"))).SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(
            () => Insert(scope.ProfileId, scope.Module, Guid.NewGuid(), true, "PendingStorage"))).SqlState);
        var row = await store.ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
        const string pendingIndex = "uq_profile_photo_pending";
        Assert.Equal(pendingIndex, (await Assert.ThrowsAsync<PostgresException>(
            () => Insert(scope.ProfileId, scope.Module, Guid.NewGuid(), false, "PendingStorage"))).ConstraintName);

        var profileRepository = new PrivateProfileRepository(db);
        await profileRepository.WithAuthorizedProfileAsync(account, scope.Module, _ => { },
            _ => store.FinalizeAsync(scope, row.Id, row.LeaseToken!.Value, default), default);
        Assert.Equal("uq_profile_photo_current", (await Assert.ThrowsAsync<PostgresException>(
            () => Insert(scope.ProfileId, scope.Module, Guid.NewGuid(), true, "Stored"))).ConstraintName);
        Assert.Equal("uq_profile_photo_request", (await Assert.ThrowsAsync<PostgresException>(
            () => Insert(scope.ProfileId, scope.Module, row.RequestId, false, "PendingStorage"))).ConstraintName);
        var databaseIndexes = await db.Database.SqlQueryRaw<string>("""
            SELECT indexname AS "Value" FROM pg_indexes
            WHERE schemaname = 'andril_identity' AND tablename = 'private_profile_photos'
              AND indexname = 'uq_profile_photo_object' AND indexdef LIKE 'CREATE UNIQUE INDEX%'
            """).ToListAsync();
        Assert.Equal("uq_profile_photo_object", Assert.Single(databaseIndexes));
    }

    [LocalPostgresFact]
    public async Task UncommittedPublicationIsInvisibleToCleanupAndConcurrentRetryIsIdempotent()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        await using var writer = local.Open();
        var store = new PrivateProfilePhotoStore(writer);
        var profiles = new PrivateProfileRepository(writer);
        var first = await store.ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
        await profiles.WithAuthorizedProfileAsync(account, scope.Module, _ => { },
            _ => store.FinalizeAsync(scope, first.Id, first.LeaseToken!.Value, default), default);
        var second = await store.ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
        var token = second.LeaseToken!.Value;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var publication = profiles.WithAuthorizedProfileAsync(account, scope.Module, _ => { }, async _ =>
        {
            var result = await store.FinalizeAsync(scope, second.Id, token, deadline.Token);
            changed.SetResult();
            await commit.Task.WaitAsync(deadline.Token);
            return result;
        }, deadline.Token);

        await using var observer = local.Open();
        var concurrent = new PrivateProfilePhotoStore(observer);
        Task<PrivatePhotoStatus>? retry = null;
        try
        {
            var reached = await Task.WhenAny(publication, changed.Task).WaitAsync(deadline.Token);
            await reached;
            Assert.False(await observer.PrivateProfilePhotos.AnyAsync(x => x.ProfileId == scope.ProfileId && x.CleanupPending, deadline.Token));
            Assert.Equal(first.Id, (await concurrent.ReadCurrentAsync(scope, deadline.Token))!.Address.UploadId);
            retry = new PrivateProfileRepository(observer).WithAuthorizedProfileAsync(account, scope.Module, _ => { },
                _ => concurrent.FinalizeAsync(scope, second.Id, token, deadline.Token), deadline.Token);
        }
        finally
        {
            commit.TrySetResult();
            await publication;
            if (retry is not null) await retry;
        }

        Assert.Equal(await publication, await retry!);
        Assert.Equal(first.Id, (await concurrent.ClaimCleanupAsync(scope, deadline.Token))!.Address.UploadId);
        Assert.Equal(second.Id, (await concurrent.ReadCurrentAsync(scope, deadline.Token))!.Address.UploadId);
    }

    [LocalPostgresFact]
    public async Task PersistedEligibilityChangeDuringStorageRejectsPublicationAndSameRequestRecovers()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        await using var db = local.Open();
        var actor = await ActorAsync(db, account);
        var storage = new ControlledObjects();
        var service = Workflow(db, actor, storage);
        var previous = await UploadAsync(service, scope.Module, Guid.NewGuid());
        var replacementRequest = Guid.NewGuid();

        storage.BeforeUpload = async () =>
        {
            await using var changed = local.Open();
            var ineligible = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-12);
            await changed.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE andril_identity.account_demographics SET date_of_birth = {ineligible} WHERE account_id = {account}
                """);
        };
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => UploadAsync(service, scope.Module, replacementRequest));
        await using var review = local.Open();
        var reviewStore = new PrivateProfilePhotoStore(review);
        var pending = (await reviewStore.FindRequestAsync(scope, replacementRequest, default))!;
        Assert.Equal(PhotoState.PendingStorage, pending.State);
        Assert.Equal(previous.UploadId, (await reviewStore.ReadCurrentAsync(scope, default))!.Address.UploadId);
        Assert.Equal(2, storage.Writes);
        Assert.Empty(storage.Deleted);

        var eligible = new DateOnly(2000, 1, 1);
        await review.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.account_demographics SET date_of_birth = {eligible} WHERE account_id = {account}
            """);
        await review.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.private_profile_photos
            SET created_at_utc = statement_timestamp() - interval '5 minutes',
                updated_at_utc = statement_timestamp() - interval '5 minutes',
                lease_until_utc = statement_timestamp() - interval '2 minutes'
            WHERE id = {pending.Id}
            """);
        var recovered = await UploadAsync(Workflow(review, actor, storage), scope.Module, replacementRequest);
        Assert.Equal(pending.Id, recovered.UploadId);
        Assert.True(recovered.IsCurrent);
        Assert.Equal(2, storage.Writes);
    }

    [LocalPostgresFact]
    public async Task IdenticalBytesAndRequestIdPublishIndependentlyToTwoProfilesOfTheSameAccount()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (learning, account) = await SeedAsync(local);
        await using var db = local.Open();
        var actor = await ActorAsync(db, account);
        var storage = new ControlledObjects();
        var service = Workflow(db, actor, storage);
        var request = Guid.NewGuid();
        var first = await UploadAsync(service, "learning", request);
        var hobbiesProfile = PrivateModuleProfile.Create(account, "hobbies", DateTimeOffset.UtcNow);
        db.PrivateProfiles.Add(hobbiesProfile);
        await db.SaveChangesAsync();

        // Controlled persisted setup. The production API still permits only one starter module.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.account_starter_memberships SET module = 'hobbies' WHERE account_id = {account}
            """);
        var second = await UploadAsync(service, "hobbies", request);
        Assert.NotEqual(learning.ProfileId, hobbiesProfile.Id);
        Assert.NotEqual(first.UploadId, second.UploadId);
        var photos = await db.PrivateProfilePhotos.AsNoTracking().OrderBy(x => x.Module).ToListAsync();
        Assert.Equal(2, photos.Count);
        Assert.All(photos, row => { Assert.Equal(request, row.RequestId); Assert.True(row.IsCurrent); });
        Assert.Equal(photos[0].OriginalContentHash, photos[1].OriginalContentHash);
        Assert.Equal(photos[0].StoredContentHash, photos[1].StoredContentHash);
        Assert.NotEqual(photos[0].ObjectKey, photos[1].ObjectKey);
        Assert.Equal(2, storage.Writes);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => service.GetContentAsync("learning", first.UploadId));
        await Assert.ThrowsAsync<ProfileMissingException>(() => service.GetContentAsync("hobbies", first.UploadId));
    }

    [LocalPostgresFact]
    public async Task ConcurrentCleanupClaimantsAcquireOneTerminalCandidateAndProtectCurrentAndPending()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        var (oldId, currentId) = await PublishTwoAsync(local, scope, account);
        await using var db = local.Open();
        var store = new PrivateProfilePhotoStore(db);
        var pending = await store.ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
        async Task<PhotoCleanupClaim?> Claim()
        {
            await using var claimant = local.Open();
            return await new PrivateProfilePhotoStore(claimant).ClaimCleanupAsync(scope, default);
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Claim()));
        var winner = Assert.Single(claims.OfType<PhotoCleanupClaim>());
        Assert.Equal(oldId, winner.Address.UploadId);
        Assert.Equal(currentId, (await store.ReadCurrentAsync(scope, default))!.Address.UploadId);
        Assert.Equal(PhotoState.PendingStorage, (await store.ReadAsync(scope, pending.Id, default))!.State);
        Assert.Null(await store.ClaimCleanupAsync(scope, default));
    }

    [LocalPostgresFact]
    public async Task ExpiredCleanupClaimIsRecoveredAndStaleOwnerCannotRecordOutcome()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        var (oldId, _) = await PublishTwoAsync(local, scope, account);
        await using var db = local.Open();
        var original = new PrivateProfilePhotoStore(db);
        var stale = Assert.IsType<PhotoCleanupClaim>(await original.ClaimCleanupAsync(scope, default));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.private_profile_photos
            SET created_at_utc = statement_timestamp() - interval '3 minutes',
                updated_at_utc = statement_timestamp() - interval '2 minutes',
                superseded_at_utc = statement_timestamp() - interval '2 minutes',
                next_cleanup_attempt_at_utc = statement_timestamp() - interval '2 minutes',
                cleanup_claim_expires_at_utc = statement_timestamp() - interval '1 minute'
            WHERE id = {oldId}
            """);

        await using var next = local.Open();
        var recovery = new PrivateProfilePhotoStore(next);
        var reclaimed = Assert.IsType<PhotoCleanupClaim>(await recovery.ClaimCleanupAsync(scope, default));
        Assert.Equal(stale.Address, reclaimed.Address);
        Assert.NotEqual(stale.Token, reclaimed.Token);
        Assert.False(await original.RecordCleanupAsync(stale, true, PhotoCleanupSettings.Approved.RetryDelay, default));
        Assert.True(await recovery.RecordCleanupAsync(reclaimed, false, PhotoCleanupSettings.Approved.RetryDelay, default));
        var saved = await next.PrivateProfilePhotos.AsNoTracking().SingleAsync(x => x.Id == oldId);
        Assert.True(saved.CleanupPending);
        Assert.Null(saved.CleanupClaimId);
        Assert.Equal(saved.UpdatedAtUtc.AddSeconds(60), saved.NextCleanupAttemptAtUtc);
    }

    [LocalPostgresFact]
    public async Task RetryScheduleAndCleanupEvidencePersistAcrossContextsAndRemainScoped()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        var (otherScope, _) = await SeedAsync(local);
        var (oldId, currentId) = await PublishTwoAsync(local, scope, account);
        await using (var db = local.Open())
        {
            var store = new PrivateProfilePhotoStore(db);
            var claim = Assert.IsType<PhotoCleanupClaim>(await store.ClaimCleanupAsync(scope, default));
            Assert.True(await store.RecordCleanupAsync(claim, false, PhotoCleanupSettings.Approved.RetryDelay, default));
        }
        await using var fresh = local.Open();
        var restarted = new PrivateProfilePhotoStore(fresh);
        var saved = await fresh.PrivateProfilePhotos.AsNoTracking().SingleAsync(x => x.Id == oldId);
        Assert.True(saved.CleanupPending);
        Assert.Equal(saved.UpdatedAtUtc.AddSeconds(60), saved.NextCleanupAttemptAtUtc);
        Assert.Null(await restarted.ClaimCleanupAsync(scope, default));
        Assert.Null(await restarted.ClaimCleanupAsync(otherScope, default));
        Assert.Equal(currentId, (await restarted.ReadCurrentAsync(scope, default))!.Address.UploadId);
    }

    [LocalPostgresFact]
    public async Task ReplacementPublishesWhileAnotherRequestDeletesOnlyItsClaimedSupersededObject()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        await using var writer = local.Open();
        await using var retryDb = local.Open();
        var actor = await ActorAsync(writer, account);
        var storage = new ControlledObjects();
        var a = Workflow(writer, actor, storage);
        var firstRequest = Guid.NewGuid();
        var first = await UploadAsync(a, scope.Module, firstRequest);
        storage.DeleteOutcome = PhotoObjectOutcome.TransientFailure;
        var second = await UploadAsync(a, scope.Module, Guid.NewGuid());
        await writer.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.private_profile_photos SET next_cleanup_attempt_at_utc = statement_timestamp()
            WHERE id = {first.UploadId}
            """);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var started = new TaskCompletionSource<PhotoObjectAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.BeforeDelete = async (address, ct) =>
        {
            if (address.UploadId != first.UploadId) return;
            started.TrySetResult(address);
            await release.Task.WaitAsync(ct);
        };
        storage.DeleteOutcome = PhotoObjectOutcome.Success;
        var retry = UploadAsync(Workflow(retryDb, actor, storage), scope.Module, firstRequest, deadline.Token);
        PrivatePhotoStatus third;
        try
        {
            var deleting = await started.Task.WaitAsync(deadline.Token);
            Assert.Equal(first.UploadId, deleting.UploadId);
            Assert.Equal(second.UploadId, (await a.GetCurrentAsync(scope.Module, deadline.Token)).UploadId);
            third = await UploadAsync(a, scope.Module, Guid.NewGuid(), deadline.Token);
            Assert.Equal(third.UploadId, (await a.GetCurrentAsync(scope.Module, deadline.Token)).UploadId);
        }
        finally
        {
            release.TrySetResult();
            await retry;
        }
        Assert.Equal(1, storage.Deleted.Count(x => x.UploadId == first.UploadId));
        Assert.DoesNotContain(storage.Deleted, x => x.UploadId == third.UploadId);
        Assert.Equal(new byte[] { 42, 7 }, await a.GetContentAsync(scope.Module, third.UploadId));
    }

    [IsolatedRolePostgresFact]
    public async Task RestrictedRuntimeUsesProposedPhotoGrantsWithoutOwnerOrClientPrivileges()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var (scope, account) = await SeedAsync(local);
        await using var db = local.Open();
        var actor = await ActorAsync(db, account);
        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var suffix = Guid.NewGuid().ToString("N");
        var owner = $"andril_m2_owner_{suffix}";
        var runtime = $"andril_m2_runtime_{suffix}";
        var client = $"andril_m2_client_{suffix}";
        var created = new List<string>();

        // All interpolated role identifiers are generated above, never read from configuration.
        async Task Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        async Task Denied(string sql) => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await Assert.ThrowsAsync<PostgresException>(() => Execute(sql))).SqlState);

        try
        {
            foreach (var role in new[] { owner, runtime, client })
            {
                await Execute($"CREATE ROLE {role} NOLOGIN NOINHERIT");
                created.Add(role);
                await Execute($"GRANT {role} TO CURRENT_USER");
            }

            // The fixture applied the migration. Transfer ownership to model a separate migration
            // owner, then apply the proposed Phase 05 photo grants from the deployment runbook.
            await Execute($"""
                GRANT USAGE, CREATE ON SCHEMA andril_identity TO {owner};
                ALTER TABLE andril_identity.private_profile_photos OWNER TO {owner};
                ALTER TABLE andril_identity."__EFMigrationsHistory" OWNER TO {owner};
                """);

            await Execute($"""
                GRANT USAGE ON SCHEMA andril_identity TO {runtime}, {client};
                GRANT SELECT ON andril_identity.accounts, andril_identity.account_demographics,
                    andril_identity.account_starter_memberships, andril_identity.account_policy_records,
                    andril_identity.private_module_profiles TO {runtime};
                GRANT UPDATE (updated_at_utc) ON andril_identity.accounts TO {runtime};
                GRANT UPDATE (updated_at_utc) ON andril_identity.account_demographics TO {runtime};
                GRANT UPDATE (display_name) ON andril_identity.private_module_profiles TO {runtime};
                CREATE POLICY test_account_read ON andril_identity.accounts FOR SELECT TO {runtime} USING (true);
                CREATE POLICY test_account_lock ON andril_identity.accounts FOR UPDATE TO {runtime} USING (true);
                CREATE POLICY test_demographics_read ON andril_identity.account_demographics FOR SELECT TO {runtime} USING (true);
                CREATE POLICY test_demographics_lock ON andril_identity.account_demographics FOR UPDATE TO {runtime} USING (true);
                CREATE POLICY test_membership_read ON andril_identity.account_starter_memberships FOR SELECT TO {runtime} USING (true);
                CREATE POLICY test_policy_read ON andril_identity.account_policy_records FOR SELECT TO {runtime} USING (true);
                CREATE POLICY test_profile_read ON andril_identity.private_module_profiles FOR SELECT TO {runtime} USING (true);
                CREATE POLICY test_profile_lock ON andril_identity.private_module_profiles FOR UPDATE TO {runtime} USING (true);

                GRANT SELECT, INSERT ON andril_identity.private_profile_photos TO {runtime};
                GRANT UPDATE (state, reason, is_current, cleanup_pending, late_write_risk, lease_token, lease_until_utc,
                    updated_at_utc, superseded_at_utc, next_cleanup_attempt_at_utc, cleanup_claim_id, cleanup_claim_expires_at_utc)
                    ON andril_identity.private_profile_photos TO {runtime};
                CREATE POLICY profile_photos_backend_select ON andril_identity.private_profile_photos
                    FOR SELECT TO {runtime} USING (true);
                CREATE POLICY profile_photos_backend_insert ON andril_identity.private_profile_photos
                    FOR INSERT TO {runtime} WITH CHECK (true);
                CREATE POLICY profile_photos_backend_update ON andril_identity.private_profile_photos
                    FOR UPDATE TO {runtime} USING (true) WITH CHECK (true);
                """);

            await Execute($"SET ROLE {runtime}");
            var storage = new ControlledObjects();
            var service = Workflow(db, actor, storage);
            var initial = await UploadAsync(service, scope.Module, Guid.NewGuid());
            Assert.True(initial.IsCurrent);
            var replacement = await UploadAsync(service, scope.Module, Guid.NewGuid());
            Assert.Equal(replacement, await service.GetCurrentAsync(scope.Module));
            Assert.Equal(initial.UploadId, Assert.Single(storage.Deleted).UploadId);
            Assert.False((await db.PrivateProfilePhotos.AsNoTracking().SingleAsync(x => x.Id == initial.UploadId)).CleanupPending);
            await Denied("CREATE TABLE andril_identity.forbidden_photo_test (id integer)");
            await Denied("DROP TABLE andril_identity.private_profile_photos");
            await Denied("SELECT * FROM andril_identity.\"__EFMigrationsHistory\"");
            await Denied("DELETE FROM andril_identity.private_profile_photos");
            await Denied("UPDATE andril_identity.accounts SET supabase_user_id = supabase_user_id");
            await Denied("UPDATE andril_identity.private_profile_photos SET object_key = object_key");

            await Execute($"RESET ROLE; SET ROLE {client}");
            await Denied("SELECT * FROM andril_identity.private_profile_photos");
            // Confirm RLS still denies visibility even if a direct SELECT grant is introduced.
            await Execute($"RESET ROLE; GRANT SELECT ON andril_identity.private_profile_photos TO {client}; SET ROLE {client}");
            Assert.False(await db.PrivateProfilePhotos.AsNoTracking().AnyAsync());
        }
        finally
        {
            await Execute("RESET ROLE");
            // Operations affect only the verified disposable database and roles created by this test.
            foreach (var role in created) await Execute($"DROP OWNED BY {role}");
            foreach (var role in created.AsEnumerable().Reverse()) await Execute($"DROP ROLE {role}");
        }
    }

    public sealed class IsolatedRolePostgresFactAttribute : FactAttribute
    {
        public IsolatedRolePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES"))
                || Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES") != "1")
                Skip = "Requires an explicitly opted-in, isolated disposable PostgreSQL cluster for role tests.";
        }
    }

    private static async Task<PhotoActor> ActorAsync(AccountsDbContext db, Guid account) =>
        new((await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == account)).SupabaseUserId);

    private static ProfilePhotoService Workflow(AccountsDbContext db, PhotoActor actor, ControlledObjects storage) =>
        new(new PrivateProfileService(actor, actor, new AccountRepository(db), new PrivateProfileRepository(db),
            actor, TimeProvider.System), actor, actor, new PrivateProfilePhotoStore(db), storage);

    private static Task<PrivatePhotoStatus> UploadAsync(IProfilePhotoService service, string module, Guid request, CancellationToken ct = default) =>
        service.UploadAsync(module, request, new MemoryStream([7]), ct);

    private sealed class PhotoActor(Guid subject) : ICurrentUser, IOnboardingIdentity,
        IPolicyCatalogProvider, IPhotoUploadPolicy, IProfileImageInspector
    {
        public Guid? UserId => subject;
        public bool IsAuthenticated => true;
        public bool IsConfirmedFor(Guid candidate) => subject == candidate;
        public PolicyCatalog GetCurrent() => new(new("v1", "https://example.test/terms", "en"),
            new("v1", "https://example.test/privacy", "en"), new("v1", "https://example.test/community", "en"));
        public int RequireMaximumBytes() => ProfilePhotoLimits.UploadBytes;
        public bool IsApprovedAndConfigured => true;
        public Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken ct) =>
            Task.FromResult(new SafeProfileImage([42, .. bytes], "image/jpeg", 300, 300));
    }

    private sealed class ControlledObjects : IPrivateProfilePhotoObjectStorage
    {
        private readonly ConcurrentDictionary<PhotoObjectAddress, byte[]> objects = new();
        internal readonly ConcurrentBag<PhotoObjectAddress> Deleted = [];
        internal int Writes;
        internal PhotoObjectOutcome DeleteOutcome = PhotoObjectOutcome.Success;
        internal Func<Task>? BeforeUpload;
        internal Func<PhotoObjectAddress, CancellationToken, Task>? BeforeDelete;
        public bool IsConfigured => true;

        public async Task<PhotoObjectOutcome> UploadAsync(StoredProfilePhoto expected, byte[] jpeg, CancellationToken ct)
        {
            if (BeforeUpload is not null) await BeforeUpload();
            if (!objects.TryAdd(expected.Address, jpeg)) return PhotoObjectOutcome.Conflict;
            Interlocked.Increment(ref Writes);
            return PhotoObjectOutcome.Success;
        }

        public Task<PhotoObjectRead> ReadAsync(StoredProfilePhoto expected, CancellationToken ct) =>
            Task.FromResult(objects.TryGetValue(expected.Address, out var bytes)
                ? new PhotoObjectRead(PhotoObjectOutcome.Success, bytes) : new(PhotoObjectOutcome.Missing));

        public async Task<PhotoObjectOutcome> DeleteAsync(PhotoObjectAddress address, CancellationToken ct)
        {
            if (BeforeDelete is not null) await BeforeDelete(address, ct);
            if (DeleteOutcome != PhotoObjectOutcome.Success) return DeleteOutcome;
            Deleted.Add(address);
            return objects.TryRemove(address, out _) ? PhotoObjectOutcome.Success : PhotoObjectOutcome.Missing;
        }
    }

    private static async Task<(Guid Old, Guid Current)> PublishTwoAsync(LocalAccountDatabase local, PhotoScope scope, Guid account)
    {
        await using var db = local.Open();
        var repository = new PrivateProfileRepository(db);
        var store = new PrivateProfilePhotoStore(db);
        async Task<PrivatePhotoStatus> Publish()
        {
            var row = await store.ReserveAsync(scope, Guid.NewGuid(), Hash, Evidence, default);
            return await repository.WithAuthorizedProfileAsync(account, scope.Module, _ => { },
                _ => store.FinalizeAsync(scope, row.Id, row.LeaseToken!.Value, default), default);
        }
        return ((await Publish()).UploadId, (await Publish()).UploadId);
    }

    private static async Task<(PhotoScope Scope, Guid Account)> SeedAsync(LocalAccountDatabase local)
    {
        await using var db = local.Open();
        var now = DateTimeOffset.UtcNow;
        var account = Account.Create(Guid.NewGuid(), now);
        var profile = PrivateModuleProfile.Create(account.Id, "learning", now);
        db.Accounts.Add(account);
        db.Demographics.Add(AccountDemographics.Create(account.Id, new(2000, 1, 1), "other", now));
        db.StarterMemberships.Add(StarterMembership.Create(account.Id, "learning", false, now));
        db.PolicyRecords.AddRange(
            AccountPolicyRecord.Create(account.Id, PolicyActions.TermsAgreed, "v1", now),
            AccountPolicyRecord.Create(account.Id, PolicyActions.PrivacyAcknowledged, "v1", now));
        db.PrivateProfiles.Add(profile);
        await db.SaveChangesAsync();
        return (new(profile.Id, profile.Module), account.Id);
    }
}
