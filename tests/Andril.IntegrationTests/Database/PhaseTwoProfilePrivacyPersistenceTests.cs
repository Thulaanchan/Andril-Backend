using Andril.Common.Accounts;
using Andril.Common.Accounts.Privacy;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Persistence.Repositories.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class PhaseTwoProfilePrivacyPersistenceTests
{
    [LocalPostgresFact]
    public async Task FailureAfterLocationInsertRollsBackNewProfileAndLocation()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        await using (var db = database.Open())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PrivateProfileRepository(db).WithProfileSetupAsync<int>(
                account.Id, "learning", new("AtomicLearner", "draft", "private"), _ => { }, TimeProvider.System, async profile =>
                {
                    await new ProfileLocationRepository(db).UpsertAsync(ProfileLocationRecord.Create(profile.Id, account.Id, "learning", "Colombo", "Western", DateTimeOffset.UtcNow));
                    throw new InvalidOperationException("Simulated additional-metadata read failure.");
                }, default));
        }
        await using var check = database.Open();
        Assert.Empty(await check.PrivateProfiles.ToListAsync());
        Assert.Empty(await check.Set<ProfileLocationRecord>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task FailedUpdatePreservesPreviouslyCommittedProfileAndLocation()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        Guid profileId;
        await using (var db = database.Open())
        {
            profileId = await new PrivateProfileRepository(db).WithProfileSetupAsync(account.Id, "learning",
                new("OriginalLearner", "original", "private"), _ => { }, TimeProvider.System, async profile =>
                {
                    await new ProfileLocationRepository(db).UpsertAsync(ProfileLocationRecord.Create(profile.Id, account.Id, "learning", "Colombo", "Western", DateTimeOffset.UtcNow));
                    return profile.Id;
                }, default);
        }
        await using (var db = database.Open())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PrivateProfileRepository(db).WithProfileSetupAsync<int>(account.Id, "learning",
                new("ChangedLearner", "changed", "private"), _ => { }, TimeProvider.System, async profile =>
                {
                    var locations = new ProfileLocationRepository(db);
                    var location = (await locations.GetByProfileIdAsync(profile.Id))!;
                    location.Update("Kandy", "Central", DateTimeOffset.UtcNow);
                    await locations.UpsertAsync(location);
                    throw new InvalidOperationException("Simulated response dependency failure.");
                }, default));
        }
        await using var check = database.Open();
        var original = await check.PrivateProfiles.SingleAsync();
        Assert.Equal(profileId, original.Id);
        Assert.Equal("OriginalLearner", original.DisplayName);
        Assert.Equal("original", original.Bio);
        Assert.Equal("Colombo", (await check.Set<ProfileLocationRecord>().SingleAsync()).City);
    }

    [LocalPostgresFact]
    public async Task RepeatedAndConcurrentSetupPreserveOneScopedProfileAndAuthoritativeResponse()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        var ids = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = database.Open();
            return await new PrivateProfileRepository(db).WithProfileSetupAsync(account.Id, "learning",
                new("ConcurrentLearner", null, "private"), _ => { }, TimeProvider.System, async profile =>
                {
                    var stored = await new ProfileLocationRepository(db).UpsertAsync(ProfileLocationRecord.Create(profile.Id, account.Id, "learning", "Colombo", null, DateTimeOffset.UtcNow));
                    Assert.Equal(profile.Id, stored.ProfileId);
                    Assert.Equal(account.Id, stored.AccountId);
                    Assert.Equal("ConcurrentLearner", profile.DisplayName);
                    return profile.Id;
                }, default);
        }));
        Assert.Single(ids.Distinct());
        await using var check = database.Open();
        Assert.Single(await check.PrivateProfiles.ToListAsync());
        Assert.Single(await check.Set<ProfileLocationRecord>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task AuthorizationFailureAndCrossModuleSetupLeaveNoWrites()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        await using var db = database.Open();
        var repository = new PrivateProfileRepository(db);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => repository.WithProfileSetupAsync(account.Id, "learning",
            new("DeniedLearner", null, "private"), _ => throw new ProfileAccessDeniedException(), TimeProvider.System, p => Task.FromResult(p.Id), default));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => repository.WithProfileSetupAsync(account.Id, "hobbies",
            new("DeniedHobby", null, "private"), _ => { }, TimeProvider.System, p => Task.FromResult(p.Id), default));
        Assert.Empty(await db.PrivateProfiles.ToListAsync());
    }

    [LocalPostgresFact]
    public async Task ConcurrentPrivacyDefaultsNeverOverwriteRestrictiveUpdate()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        var restricted = UserPrivacySettings.CreateDefault(account.Id, DateTimeOffset.UtcNow);
        restricted.Update("private", true, "disabled", false, false, DateTimeOffset.UtcNow);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            await using var db = database.Open();
            var repository = new UserPrivacySettingsRepository(db);
            if (index == 5) await repository.UpsertAsync(restricted);
            else await repository.GetOrCreateAsync(account.Id, DateTimeOffset.UtcNow);
        }));
        await using var check = database.Open();
        var stored = await new UserPrivacySettingsRepository(check).GetOrCreateAsync(account.Id, DateTimeOffset.UtcNow);
        Assert.Equal("private", stored.ProfileDiscoverability);
        Assert.True(stored.GhostModeEnabled);
        Assert.Equal("disabled", stored.DirectMessagingPolicy);
        Assert.False(stored.AllowFriendRequests);
        Assert.False(stored.ShowActiveModule);
        Assert.Single(await check.PrivacySettings.ToListAsync());
    }

    [LocalPostgresFact]
    public async Task FullPrivacyReplacementsRemainAtomicAndAccountsStayIsolated()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        var other = await Seed(database);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            await using var db = database.Open();
            var settings = UserPrivacySettings.CreateDefault(account.Id, DateTimeOffset.UtcNow);
            var restricted = index % 2 == 0;
            settings.Update(restricted ? "private" : "public", restricted, restricted ? "disabled" : "everyone", !restricted, !restricted, DateTimeOffset.UtcNow);
            await new UserPrivacySettingsRepository(db).UpsertAsync(settings);
        }));
        await using var check = database.Open();
        var stored = (await new UserPrivacySettingsRepository(check).GetByAccountIdAsync(account.Id))!;
        var privateMode = stored.ProfileDiscoverability == "private";
        Assert.Equal(privateMode, stored.GhostModeEnabled);
        Assert.Equal(privateMode ? "disabled" : "everyone", stored.DirectMessagingPolicy);
        Assert.Equal(!privateMode, stored.AllowFriendRequests);
        Assert.Equal(!privateMode, stored.ShowActiveModule);
        Assert.Null(await new UserPrivacySettingsRepository(check).GetByAccountIdAsync(other.Id));
    }

    [LocalPostgresFact]
    public async Task DatabaseRejectsForeignAccountAndModuleMetadataWithoutChangingOwnedRows()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var owner = await Seed(database);
        var foreign = await Seed(database);
        Guid profileId;
        await using (var db = database.Open())
            profileId = (await new PrivateProfileRepository(db).GetOrCreateAsync(owner.Id, "learning", _ => { }, TimeProvider.System)).Id;
        foreach (var scope in new[] { (foreign.Id, "learning"), (owner.Id, "hobbies") })
        {
            await using var db = database.Open();
            await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => new ProfileLocationRepository(db).UpsertAsync(
                ProfileLocationRecord.Create(profileId, scope.Id, scope.Item2, "Colombo", null, DateTimeOffset.UtcNow)));
            await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => new AdditionalProfilePhotoRepository(db).UpsertSlotPhotoAsync(
                AdditionalProfilePhotoRecord.Create(profileId, scope.Id, scope.Item2, 1, "private/key", null, "image/jpeg", 100, DateTimeOffset.UtcNow)));
        }
        await using var check = database.Open();
        Assert.Empty(await check.Set<ProfileLocationRecord>().ToListAsync());
        Assert.Empty(await check.Set<AdditionalProfilePhotoRecord>().ToListAsync());
        Assert.Equal(owner.Id, (await check.PrivateProfiles.SingleAsync()).AccountId);
    }

    [LocalPostgresFact]
    public async Task OwnershipMigrationUpgradeAndRollbackPreserveValidExistingData()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        await using var db = database.Open();
        var profile = await new PrivateProfileRepository(db).GetOrCreateAsync(account.Id, "learning", _ => { }, TimeProvider.System);
        await new ProfileLocationRepository(db).UpsertAsync(ProfileLocationRecord.Create(profile.Id, account.Id, "learning", "Colombo", "Western", DateTimeOffset.UtcNow));
        await new AdditionalProfilePhotoRepository(db).UpsertSlotPhotoAsync(AdditionalProfilePhotoRecord.Create(profile.Id, account.Id, "learning", 1, "private/key", null, "image/jpeg", 100, DateTimeOffset.UtcNow));
        await new UserPrivacySettingsRepository(db).GetOrCreateAsync(account.Id, DateTimeOffset.UtcNow);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261126150000_AddProfileLocationsPhotosAndPrivacySettings");
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(profile.Id, (await db.PrivateProfiles.SingleAsync()).Id);
        Assert.Equal("Colombo", (await db.Set<ProfileLocationRecord>().SingleAsync()).City);
        Assert.Equal("private/key", (await db.Set<AdditionalProfilePhotoRecord>().SingleAsync()).ObjectKey);
        Assert.Equal(account.Id, (await db.PrivacySettings.SingleAsync()).AccountId);
    }

    [LocalPostgresFact]
    public async Task ConflictingMetadataUpsertsCannotReplaceOrDiscloseAnExistingForeignScope()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var owner = await Seed(database);
        var foreign = await Seed(database);
        await using var db = database.Open();
        var profile = await new PrivateProfileRepository(db).GetOrCreateAsync(owner.Id, "learning", _ => { }, TimeProvider.System);
        var locations = new ProfileLocationRepository(db);
        var photos = new AdditionalProfilePhotoRepository(db);
        await locations.UpsertAsync(ProfileLocationRecord.Create(profile.Id, owner.Id, "learning", "Colombo", null, DateTimeOffset.UtcNow));
        await photos.UpsertSlotPhotoAsync(AdditionalProfilePhotoRecord.Create(profile.Id, owner.Id, "learning", 1, "original-key", null, "image/jpeg", 100, DateTimeOffset.UtcNow));
        foreach (var scope in new[] { (foreign.Id, "learning"), (owner.Id, "hobbies") })
        {
            await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => locations.UpsertAsync(ProfileLocationRecord.Create(profile.Id, scope.Id, scope.Item2, "Kandy", null, DateTimeOffset.UtcNow)));
            await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => photos.UpsertSlotPhotoAsync(AdditionalProfilePhotoRecord.Create(profile.Id, scope.Id, scope.Item2, 1, "foreign-key", null, "image/jpeg", 200, DateTimeOffset.UtcNow)));
        }
        Assert.Equal("Colombo", (await locations.GetByProfileIdAsync(profile.Id))!.City);
        Assert.Equal("original-key", (await photos.GetByProfileAndSlotAsync(profile.Id, 1))!.ObjectKey);
    }

    private static async Task<Account> Seed(LocalAccountDatabase database)
    {
        var account = Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await using var db = database.Open();
        db.Accounts.Add(account);
        db.StarterMemberships.Add(StarterMembership.Create(account.Id, "learning", false, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        return account;
    }
}
