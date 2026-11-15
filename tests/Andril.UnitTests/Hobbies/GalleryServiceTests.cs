
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Services;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class GalleryServiceTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid OtherOwnerId = Guid.NewGuid();

    private static GalleryMedia CreateMedia(
        Guid ownerId,
        string module = "Hobbies",
        bool isDeleted = false)
    {
        return new GalleryMedia
        {
            Id = Guid.NewGuid(),
            OwnerModuleProfileId = ownerId,
            Module = module,
            Title = "Nature Photo",
            Description = "Beautiful location",
            Category = "Nature",
            MediaType = "Photo",
            StorageObjectKey = "private/test-photo.jpg",
            IsPrivate = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsDeleted = isDeleted
        };
    }

    private static GalleryService CreateService(
        FakeGalleryRepository repository,
        Guid? profileId = null,
        string module = "Hobbies",
        bool active = true)
    {
        var actor = new GalleryActor(
            profileId ?? OwnerId,
            module,
            active);

        return new GalleryService(
            repository,
            new FakeGalleryActorContext(actor));
    }

    [Fact]
    public async Task GetMine_ReturnsOnlyOwnActiveHobbiesMedia()
    {
        var repository = new FakeGalleryRepository();

        var ownMedia = CreateMedia(OwnerId);

        repository.Media.Add(ownMedia);
        repository.Media.Add(CreateMedia(OtherOwnerId));
        repository.Media.Add(CreateMedia(OwnerId, "Learning"));
        repository.Media.Add(CreateMedia(OwnerId, isDeleted: true));

        var service = CreateService(repository);

        var result = await service.GetMineAsync();

        var photo = Assert.Single(result);

        Assert.Equal(ownMedia.Id, photo.Id);
        Assert.Equal(ownMedia.Title, photo.Title);
    }

    [Fact]
    public async Task GetMine_InactiveProfile_IsDenied()
    {
        var repository = new FakeGalleryRepository();

        var service = CreateService(
            repository,
            active: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetMineAsync());
    }

    [Fact]
    public async Task GetMine_WrongModule_IsDenied()
    {
        var repository = new FakeGalleryRepository();

        var service = CreateService(
            repository,
            module: "Learning");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetMineAsync());
    }

    [Fact]
    public async Task GetMineById_OtherOwner_ReturnsNull()
    {
        var repository = new FakeGalleryRepository();

        var otherMedia = CreateMedia(OtherOwnerId);

        repository.Media.Add(otherMedia);

        var service = CreateService(repository);

        var result = await service.GetMineByIdAsync(
            otherMedia.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMineById_DeletedMedia_ReturnsNull()
    {
        var repository = new FakeGalleryRepository();

        var deletedMedia = CreateMedia(
            OwnerId,
            isDeleted: true);

        repository.Media.Add(deletedMedia);

        var service = CreateService(repository);

        var result = await service.GetMineByIdAsync(
            deletedMedia.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateTitle_EmptyTitle_IsRejected()
    {
        var repository = new FakeGalleryRepository();

        var media = CreateMedia(OwnerId);

        repository.Media.Add(media);

        var service = CreateService(repository);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateTitleAsync(
                media.Id,
                "   "));

        Assert.Equal("Nature Photo", media.Title);
    }

    [Fact]
    public async Task UpdateTitle_ValidTitle_IsTrimmedAndSaved()
    {
        var repository = new FakeGalleryRepository();

        var media = CreateMedia(OwnerId);

        repository.Media.Add(media);

        var service = CreateService(repository);

        var updated = await service.UpdateTitleAsync(
            media.Id,
            "  Mountain View  ");

        Assert.True(updated);
        Assert.Equal("Mountain View", media.Title);
    }

    [Fact]
    public async Task UpdateTitle_OtherOwner_IsDenied()
    {
        var repository = new FakeGalleryRepository();

        var media = CreateMedia(OtherOwnerId);

        repository.Media.Add(media);

        var service = CreateService(repository);

        var updated = await service.UpdateTitleAsync(
            media.Id,
            "Changed Title");

        Assert.False(updated);
        Assert.Equal("Nature Photo", media.Title);
    }

    [Fact]
    public async Task SelectCover_OtherOwner_IsDenied()
    {
        var repository = new FakeGalleryRepository();

        var media = CreateMedia(OtherOwnerId);

        repository.Media.Add(media);

        var service = CreateService(repository);

        var result = await service.SelectCoverAsync(media.Id);

        Assert.False(result);
    }

    [Fact]
    public async Task SelectCover_OwnPhoto_Succeeds()
    {
        var repository = new FakeGalleryRepository();

        var media = CreateMedia(OwnerId);

        repository.Media.Add(media);

        var service = CreateService(repository);

        var selected = await service.SelectCoverAsync(
            media.Id);

        var photo = await service.GetMineByIdAsync(
            media.Id);

        Assert.True(selected);
        Assert.NotNull(photo);
        Assert.True(photo.IsCover);
    }

    private sealed class FakeGalleryActorContext
        : IGalleryActorContext
    {
        public FakeGalleryActorContext(GalleryActor actor)
        {
            Current = actor;
        }

        public GalleryActor? Current { get; }
    }

    private sealed class FakeGalleryRepository
        : IGalleryRepository
    {
        public List<GalleryMedia> Media { get; } = new();

        private readonly Dictionary<Guid, Guid> _covers = new();

        public Task<IReadOnlyList<GalleryMedia>>
            GetOwnerGalleryAsync(
                Guid ownerModuleProfileId,
                CancellationToken cancellationToken = default)
        {
            IReadOnlyList<GalleryMedia> result = Media
                .Where(x =>
                    x.OwnerModuleProfileId == ownerModuleProfileId)
                .ToList();

            return Task.FromResult(result);
        }

        public Task<GalleryMedia?> GetOwnedByIdAsync(
            Guid mediaId,
            Guid ownerModuleProfileId,
            CancellationToken cancellationToken = default)
        {
            var media = Media.FirstOrDefault(x =>
                x.Id == mediaId &&
                x.OwnerModuleProfileId == ownerModuleProfileId);

            return Task.FromResult(media);
        }

        public Task<Guid?> GetCoverIdAsync(
            Guid ownerModuleProfileId,
            CancellationToken cancellationToken = default)
        {
            Guid? cover = _covers.TryGetValue(
                ownerModuleProfileId,
                out var id)
                ? id
                : null;

            return Task.FromResult(cover);
        }

        public Task<bool> UpdateTitleAsync(
            Guid mediaId,
            Guid ownerModuleProfileId,
            string title,
            CancellationToken cancellationToken = default)
        {
            var media = Media.FirstOrDefault(x =>
                x.Id == mediaId &&
                x.OwnerModuleProfileId == ownerModuleProfileId);

            if (media is null)
                return Task.FromResult(false);

            media.Title = title;

            return Task.FromResult(true);
        }

        public Task<bool> SetCoverAsync(
            Guid mediaId,
            Guid ownerModuleProfileId,
            CancellationToken cancellationToken = default)
        {
            var exists = Media.Any(x =>
                x.Id == mediaId &&
                x.OwnerModuleProfileId == ownerModuleProfileId);

            if (!exists)
                return Task.FromResult(false);

            _covers[ownerModuleProfileId] = mediaId;

            return Task.FromResult(true);
        }
    }
}
