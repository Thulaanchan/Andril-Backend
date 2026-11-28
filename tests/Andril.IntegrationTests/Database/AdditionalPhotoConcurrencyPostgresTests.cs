using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.Repositories.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class AdditionalPhotoConcurrencyPostgresTests
{
    [LocalPostgresFact]
    public async Task ConcurrentMetadataReplacementsCannotExceedExistingThreeSlotConstraint()
    {
        await using var local = await LocalAccountDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var account = Account.Create(Guid.NewGuid(), now);
        var profile = PrivateModuleProfile.Create(account.Id, "learning", now);
        await using (var seed = local.Open())
        {
            seed.Accounts.Add(account); seed.PrivateProfiles.Add(profile); await seed.SaveChangesAsync();
        }
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            await using var db = local.Open();
            await new AdditionalProfilePhotoRepository(db).UpsertSlotPhotoAsync(AdditionalProfilePhotoRecord.Create(
                profile.Id, account.Id, "learning", index % 3 + 1, "private/" + Guid.NewGuid().ToString("N"),
                null, "image/jpeg", 100, now));
        }));
        await using var check = local.Open();
        var photos = await check.AdditionalPhotos.ToListAsync();
        Assert.Equal(3, photos.Count);
        Assert.Equal(new[] { 1, 2, 3 }, photos.Select(photo => photo.SlotIndex).OrderBy(slot => slot));
        Assert.All(photos, photo => { Assert.Equal(account.Id, photo.AccountId); Assert.Equal("learning", photo.Module); });
        // Metadata-only evidence: this does not activate an additional-photo upload
        // endpoint or claim provider finalization, delivery or cleanup acceptance.
    }
}
