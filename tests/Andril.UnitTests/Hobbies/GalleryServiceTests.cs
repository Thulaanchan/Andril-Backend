
using System.Text.Json;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Services;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class GalleryServiceTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid OtherOwnerId = Guid.NewGuid();
    private static readonly Guid SerializationOwnerId = new("11111111-1111-4111-8111-111111111111");
    private const string PrivateStoragePath = "private/synthetic-gallery/";

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
    public async Task GetMineById_RejectsDifferentMediaReturnedForTheSameOwner()
    {
        var media = CreateMedia(OwnerId);
        var repository = new MetadataRepositoryProbe(media);
        var service = new GalleryService(repository,
            new FakeGalleryActorContext(new GalleryActor(OwnerId, "Hobbies", true)));

        Assert.Null(await service.GetMineByIdAsync(Guid.NewGuid()));
        Assert.Equal(new[] { "detail" }, repository.Calls);
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

    [Fact]
    public async Task GetMine_SerializedArrayContainsOnlyOrdinaryGalleryMetadata()
    {
        var repository = new FakeGalleryRepository();
        var first = CreateSerializationMedia(new Guid("22222222-2222-4222-8222-222222222222"), "Trail");
        var second = CreateSerializationMedia(new Guid("33333333-3333-4333-8333-333333333333"), "Summit");
        repository.Media.AddRange([first, second]);
        Assert.True(await repository.SetCoverAsync(second.Id, SerializationOwnerId));
        var service = CreateService(repository, profileId: SerializationOwnerId);

        var photos = await service.GetMineAsync();
        var json = JsonSerializer.Serialize(photos, JsonSerializerOptions.Web);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Collection(document.RootElement.EnumerateArray(),
            photo => AssertOrdinaryPhotoJson(photo, first, isCover: false),
            photo => AssertOrdinaryPhotoJson(photo, second, isCover: true));
        Assert.DoesNotContain(PrivateStoragePath, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMineById_SerializedObjectContainsOnlyOrdinaryGalleryMetadata()
    {
        var repository = new FakeGalleryRepository();
        var media = CreateSerializationMedia(new Guid("44444444-4444-4444-8444-444444444444"), "River");
        repository.Media.Add(media);
        var service = CreateService(repository, profileId: SerializationOwnerId);

        var photo = await service.GetMineByIdAsync(media.Id);
        Assert.NotNull(photo);
        var json = JsonSerializer.Serialize(photo, JsonSerializerOptions.Web);
        using var document = JsonDocument.Parse(json);

        AssertOrdinaryPhotoJson(document.RootElement, media, isCover: false);
    }

    [Theory]
    [InlineData("Learning")]
    [InlineData("Social")]
    [InlineData("social")]
    [InlineData("Dating")]
    public async Task WrongModuleActorCannotReadDetailsOrWriteMetadata(string module)
    {
        // Social/social and the legacy Dating label are non-Hobbies claims, not extra modules.
        var repository = new MetadataRepositoryProbe(CreateMedia(OwnerId));
        var service = new GalleryService(repository, new FakeGalleryActorContext(new(OwnerId, module, true)));
        foreach (var operation in new[] { "detail", "title", "select" })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InvokeMetadataAsync(service, repository.Media!.Id, operation));
        if (module != "Learning") // Existing GetMine_WrongModule_IsDenied covers this listing case.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetMineAsync());
        Assert.Empty(repository.Calls);
        Assert.Equal(0, repository.TitleWrites);
        Assert.Equal(0, repository.CoverWrites);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("Learning")]
    [InlineData("Social")]
    [InlineData("foreign owner")]
    public async Task IneligibleMediaReturnedByRepositoryNeverReachesMutations(string reason)
    {
        var media = CreateMedia(reason == "foreign owner" ? OtherOwnerId : OwnerId,
            module: reason is "Learning" or "Social" ? reason : "Hobbies", isDeleted: reason == "deleted");
        // Return even foreign records to exercise the service's own defense in depth.
        var repository = new MetadataRepositoryProbe(media);
        var service = new GalleryService(repository, new FakeGalleryActorContext(new(OwnerId, "Hobbies", true)));
        Assert.False(await service.UpdateTitleAsync(media.Id, "Unpermitted update"));
        Assert.False(await service.SelectCoverAsync(media.Id));
        Assert.Equal(new[] { "detail", "detail" }, repository.Calls);
        Assert.All(repository.Owners, owner => Assert.Equal(OwnerId, owner));
        Assert.Equal("Nature Photo", media.Title);
        Assert.Equal(0, repository.TitleWrites);
        Assert.Equal(0, repository.CoverWrites);
    }

    [Theory]
    [InlineData("Learning")]
    [InlineData("Social")]
    public async Task WrongModuleDetailIsDeniedBeforeCoverLookup(string module)
    {
        var repository = new MetadataRepositoryProbe(CreateMedia(OwnerId, module));
        var service = new GalleryService(repository, new FakeGalleryActorContext(new(OwnerId, "Hobbies", true)));
        Assert.Null(await service.GetMineByIdAsync(repository.Media!.Id));
        Assert.Equal(new[] { "detail" }, repository.Calls);
    }

    [Theory]
    [InlineData("list", "list")]
    [InlineData("list", "cover")]
    [InlineData("detail", "detail")]
    [InlineData("detail", "cover")]
    [InlineData("title", "detail")]
    [InlineData("title", "title")]
    [InlineData("select", "detail")]
    [InlineData("select", "select")]
    public async Task DependencyFailureCannotBecomeSuccessfulMetadataOperation(string operation, string failAt)
    {
        var repository = new MetadataRepositoryProbe(CreateMedia(OwnerId)) { FailAt = failAt };
        var service = new GalleryService(repository, new FakeGalleryActorContext(new(OwnerId, "Hobbies", true)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeMetadataAsync(service, repository.Media!.Id, operation));
        Assert.Same(repository.Failure, error);
        Assert.Equal(failAt, repository.Calls[^1]);
        Assert.Equal(0, repository.TitleWrites);
        Assert.Equal(0, repository.CoverWrites);
        Assert.Equal("Nature Photo", repository.Media!.Title);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("detail")]
    [InlineData("title")]
    [InlineData("select")]
    public async Task PreCancelledMetadataOperationCannotReturnSuccess(string operation)
    {
        var repository = new MetadataRepositoryProbe(CreateMedia(OwnerId)) { HonorCancellation = false };
        var service = new GalleryService(repository, new FakeGalleryActorContext(new(OwnerId, "Hobbies", true)));
        var token = new CancellationToken(canceled: true);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeMetadataAsync(service, repository.Media!.Id, operation, token));
        Assert.Equal(token, error.CancellationToken);
        Assert.Equal(0, repository.TitleWrites);
        Assert.Equal(0, repository.CoverWrites);
    }

    [Theory]
    [InlineData("list", true)]
    [InlineData("detail", true)]
    [InlineData("title", true)]
    [InlineData("select", true)]
    [InlineData("list", false)]
    [InlineData("detail", false)]
    [InlineData("title", false)]
    [InlineData("select", false)]
    public async Task CancellationDuringReadMustStopMetadataResultAndFurtherWork(string operation, bool dependencyHonorsCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new MetadataRepositoryProbe(CreateMedia(OwnerId))
        {
            PauseAt = operation == "list" ? "list" : "detail",
            HonorCancellation = dependencyHonorsCancellation
        };
        var service = new GalleryService(repository, new FakeGalleryActorContext(new(OwnerId, "Hobbies", true)));
        var attempt = InvokeMetadataAsync(service, repository.Media!.Id, operation, cancellation.Token);
        try
        {
            await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(attempt.IsCompleted);
            cancellation.Cancel();
            repository.Release.SetResult();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(new[] { repository.PauseAt }, repository.Calls);
            Assert.All(repository.Tokens, token => Assert.Equal(cancellation.Token, token));
            Assert.Equal(0, repository.TitleWrites);
            Assert.Equal(0, repository.CoverWrites);
        }
        finally
        {
            repository.Release.TrySetResult();
        }
    }

    private static Task InvokeMetadataAsync(GalleryService service, Guid mediaId, string operation,
        CancellationToken cancellationToken = default) => operation switch
    {
        "list" => service.GetMineAsync(cancellationToken),
        "detail" => service.GetMineByIdAsync(mediaId, cancellationToken),
        "title" => service.UpdateTitleAsync(mediaId, "Changed title", cancellationToken),
        "select" => service.SelectCoverAsync(mediaId, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private sealed class MetadataRepositoryProbe(GalleryMedia? media) : IGalleryRepository
    {
        public GalleryMedia? Media { get; } = media;
        public List<string> Calls { get; } = [];
        public List<Guid> Owners { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public string? FailAt { get; init; }
        public string? PauseAt { get; init; }
        public bool HonorCancellation { get; init; } = true;
        public InvalidOperationException Failure { get; } = new("Synthetic metadata dependency failure.");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TitleWrites { get; private set; }
        public int CoverWrites { get; private set; }

        private async Task<T> RespondAsync<T>(string operation, Guid owner, CancellationToken token, T value)
        {
            Calls.Add(operation);
            Owners.Add(owner);
            Tokens.Add(token);
            if (operation == PauseAt)
            {
                Started.TrySetResult();
                await Release.Task; // Deliberately allow a late successful response when configured.
            }
            if (operation == FailAt) throw Failure;
            if (HonorCancellation) token.ThrowIfCancellationRequested();
            return value;
        }

        public Task<IReadOnlyList<GalleryMedia>> GetOwnerGalleryAsync(Guid ownerModuleProfileId, CancellationToken cancellationToken = default) =>
            RespondAsync<IReadOnlyList<GalleryMedia>>("list", ownerModuleProfileId, cancellationToken, Media is null ? [] : [Media]);
        public Task<GalleryMedia?> GetOwnedByIdAsync(Guid mediaId, Guid ownerModuleProfileId, CancellationToken cancellationToken = default) =>
            RespondAsync("detail", ownerModuleProfileId, cancellationToken, Media);
        public Task<Guid?> GetCoverIdAsync(Guid ownerModuleProfileId, CancellationToken cancellationToken = default) =>
            RespondAsync<Guid?>("cover", ownerModuleProfileId, cancellationToken, null);
        public async Task<bool> UpdateTitleAsync(Guid mediaId, Guid ownerModuleProfileId, string title, CancellationToken cancellationToken = default)
        {
            await RespondAsync("title", ownerModuleProfileId, cancellationToken, true);
            TitleWrites++;
            Media!.Title = title;
            return true;
        }
        public async Task<bool> SetCoverAsync(Guid mediaId, Guid ownerModuleProfileId, CancellationToken cancellationToken = default)
        {
            await RespondAsync("select", ownerModuleProfileId, cancellationToken, true);
            CoverWrites++;
            return true;
        }
    }

    private static GalleryMedia CreateSerializationMedia(Guid id, string title) => new()
    {
        Id = id,
        OwnerModuleProfileId = SerializationOwnerId,
        Title = title,
        Description = "Synthetic Gallery description",
        Category = "Nature",
        MediaType = "Photo",
        StorageObjectKey = $"{PrivateStoragePath}{id:N}.jpg",
        IsPrivate = true,
        CreatedAtUtc = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero)
    };

    private static void AssertOrdinaryPhotoJson(JsonElement photo, GalleryMedia media, bool isCover)
    {
        Assert.Equal(JsonValueKind.Object, photo.ValueKind);
        Assert.Equal(new[] { "category", "createdAtUtc", "description", "id", "isCover", "isPrivate", "mediaType", "title" },
            photo.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(media.Id, photo.GetProperty("id").GetGuid());
        Assert.Equal(media.Title, photo.GetProperty("title").GetString());
        Assert.Equal(media.Description, photo.GetProperty("description").GetString());
        Assert.Equal(media.Category, photo.GetProperty("category").GetString());
        Assert.Equal(media.MediaType, photo.GetProperty("mediaType").GetString());
        Assert.Equal(media.CreatedAtUtc, photo.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.Equal(media.IsPrivate, photo.GetProperty("isPrivate").GetBoolean());
        Assert.Equal(isCover, photo.GetProperty("isCover").GetBoolean());

        var json = photo.GetRawText();
        Assert.DoesNotContain(media.StorageObjectKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateStoragePath, json, StringComparison.Ordinal);
        Assert.DoesNotContain(media.OwnerModuleProfileId.ToString(), json, StringComparison.OrdinalIgnoreCase);
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
