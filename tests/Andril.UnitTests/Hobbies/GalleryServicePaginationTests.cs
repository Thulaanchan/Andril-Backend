using System.Text.Json;
using Andril.Hobbies.DTOs;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Services;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class GalleryServicePaginationTests
{
    private static readonly Guid Owner = new("11111111-1111-4111-8111-111111111111");
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    private static GalleryMedia Media() => new()
    {
        Id = Guid.NewGuid(), OwnerModuleProfileId = Owner, CreatedAtUtc = Timestamp,
        Title = "Synthetic photo", StorageObjectKey = "private/synthetic/never-return.jpg"
    };
    private static GalleryService Service(Probe probe, Actor? actor = null) => new(probe, actor ?? new Actor(), probe);

    [Fact]
    public async Task PagedMetadataUsesActorScopeForwardsBoundsPositionAndTokenWithoutUnboundedRead()
    {
        var media = Media(); var next = new GalleryPagePosition(Owner, "Hobbies", Timestamp, media.Id);
        var after = next with { Id = Guid.NewGuid() };
        var probe = new Probe { Page = new([media], next), Cover = media.Id };
        using var cancellation = new CancellationTokenSource();
        var result = await Service(probe).GetMinePageAsync(1, 3, after, cancellation.Token);
        Assert.Equal((Owner, 1, 3, after, cancellation.Token), Assert.Single(probe.PageCalls));
        Assert.Equal(Owner, Assert.Single(probe.CoverOwners));
        Assert.Equal(cancellation.Token, probe.CoverToken);
        Assert.Same(next, result.Next);
        Assert.True(Assert.Single(result.Items).IsCover);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Items, JsonSerializerOptions.Web));
        Assert.Equal(new[] { "category", "createdAtUtc", "description", "id", "isCover", "isPrivate", "mediaType", "title" },
            json.RootElement[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(media.StorageObjectKey, json.RootElement.GetRawText());
        Assert.DoesNotContain(Owner.ToString(), json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("foreign-module")]
    [InlineData("empty")]
    public async Task IneligibleActorCannotReachPagingOrCoverPort(string reason)
    {
        var actor = new Actor { Current = reason == "missing" ? null : new(
            reason == "empty" ? Guid.Empty : Owner, reason == "foreign-module" ? "Social" : "Hobbies", reason != "inactive") };
        var probe = new Probe();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(probe, actor).GetMinePageAsync(1, 3));
        Assert.Empty(probe.PageCalls); Assert.Empty(probe.CoverOwners);
    }

    [Fact]
    public async Task ActorIsReadAgainForEveryPageAndRevocationCannotReuseContinuation()
    {
        var actor = new Actor(); var media = Media();
        var probe = new Probe { Page = new([media], new(Owner, "Hobbies", Timestamp, media.Id)) };
        var service = Service(probe, actor);
        var first = await service.GetMinePageAsync(1, 3);
        actor.Current = actor.Current! with { IsActive = false };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetMinePageAsync(1, 3, first.Next));
        Assert.Single(probe.PageCalls);
        // Synthetic context changes verify consumption, not production M2 revocation integration.
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("module")]
    [InlineData("id")]
    [InlineData("precision")]
    public async Task InvalidOrForeignContinuationIsRejectedBeforeDependencies(string reason)
    {
        var position = new GalleryPagePosition(Owner, "Hobbies", Timestamp, Guid.NewGuid());
        position = reason switch
        {
            "owner" => position with { OwnerModuleProfileId = Guid.NewGuid() },
            "module" => position with { Module = "Learning" },
            "id" => position with { Id = Guid.Empty },
            _ => position with { Timestamp = Timestamp.AddTicks(1) }
        };
        var probe = new Probe();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service(probe).GetMinePageAsync(1, 3, position));
        Assert.DoesNotContain(position.OwnerModuleProfileId.ToString(), error.Message);
        Assert.Empty(probe.PageCalls); Assert.Empty(probe.CoverOwners);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(4, 3)]
    [InlineData(1, 0)]
    [InlineData(1, int.MaxValue)]
    public async Task InvalidTestBoundsCannotReachDependencies(int size, int maximum)
    {
        var probe = new Probe();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service(probe).GetMinePageAsync(size, maximum));
        Assert.Empty(probe.PageCalls); Assert.Empty(probe.CoverOwners);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("module")]
    [InlineData("deleted")]
    [InlineData("continuation")]
    [InlineData("oversize")]
    public async Task InvalidPortResultsFailClosedWithoutMetadataOrCoverLookup(string reason)
    {
        var media = Media();
        if (reason == "owner") media.OwnerModuleProfileId = Guid.NewGuid();
        if (reason == "module") media.Module = "Learning";
        if (reason == "deleted") media.IsDeleted = true;
        var probe = new Probe { Page = new(reason == "oversize" ? [media, Media()] : [media],
            reason == "continuation" ? new(Guid.NewGuid(), "Hobbies", Timestamp, media.Id) : null) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(probe).GetMinePageAsync(1, 3));
        Assert.Equal("Gallery page retrieval is unavailable.", error.Message);
        Assert.Null(error.InnerException); Assert.Empty(probe.CoverOwners);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyAndFinalPagesPreserveNullContinuation(bool final)
    {
        var probe = new Probe { Page = new(final ? [Media()] : [], null) };
        var result = await Service(probe).GetMinePageAsync(2, 3);
        Assert.Equal(final ? 1 : 0, result.Items.Count); Assert.Null(result.Next);
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("page")]
    [InlineData("cover")]
    public async Task CancellationCannotReturnLateSuccessfulMetadata(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new Probe { AfterPage = stage == "page" ? cancellation.Cancel : null,
            AfterCover = stage == "cover" ? cancellation.Cancel : null };
        if (stage == "pre") cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(probe).GetMinePageAsync(1, 3, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        if (stage != "cover") Assert.Empty(probe.CoverOwners);
        if (stage == "pre") Assert.Empty(probe.PageCalls);
    }

    [Fact]
    public async Task CancellationWhilePagingIsPendingPropagatesWithoutCoverRead()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new Probe { WaitForCancellation = true };
        var pending = Service(probe).GetMinePageAsync(1, 3, cancellationToken: cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Empty(probe.CoverOwners);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("cover")]
    public async Task DependencyFailureCannotBecomeAnEmptyPageOrExposePrivateDetails(string stage)
    {
        var probe = new Probe { FailAt = stage };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(probe).GetMinePageAsync(1, 3));
        Assert.Equal("Gallery page retrieval is unavailable.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
        if (stage == "page") Assert.Empty(probe.CoverOwners);
    }

    [Fact]
    public async Task LegacyConstructorDoesNotEnablePagingOrFallBackToUnboundedRead()
    {
        var probe = new Probe();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GalleryService(probe, new Actor()).GetMinePageAsync(1, 3));
        Assert.Empty(probe.PageCalls); Assert.Empty(probe.CoverOwners);
    }

    private sealed class Actor : IGalleryActorContext
    {
        public GalleryActor? Current { get; set; } = new(Owner, "Hobbies", true);
    }

    private sealed class Probe : IGalleryRepository, IGalleryPageRepository
    {
        internal GalleryPage Page { get; init; } = new([], null);
        internal Guid? Cover { get; init; }
        internal string? FailAt { get; init; }
        internal Action? AfterPage { get; init; }
        internal Action? AfterCover { get; init; }
        internal bool WaitForCancellation { get; init; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<(Guid Owner, int Size, int Maximum, GalleryPagePosition? After, CancellationToken Token)> PageCalls { get; } = [];
        internal List<Guid> CoverOwners { get; } = [];
        internal CancellationToken CoverToken { get; private set; }
        public async Task<GalleryPage> GetOwnerGalleryPageAsync(Guid authorizedOwnerModuleProfileId, int pageSize,
            int trustedTestOnlyMaximum, GalleryPagePosition? after = null, CancellationToken cancellationToken = default)
        {
            PageCalls.Add((authorizedOwnerModuleProfileId, pageSize, trustedTestOnlyMaximum, after, cancellationToken));
            Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            AfterPage?.Invoke();
            if (FailAt == "page") throw new InvalidOperationException("private SQL/storage details");
            return Page;
        }
        public Task<Guid?> GetCoverIdAsync(Guid ownerModuleProfileId, CancellationToken cancellationToken = default)
        {
            CoverOwners.Add(ownerModuleProfileId); CoverToken = cancellationToken; AfterCover?.Invoke();
            if (FailAt == "cover") throw new InvalidOperationException("private SQL/storage details");
            return Task.FromResult(Cover);
        }
        public Task<IReadOnlyList<GalleryMedia>> GetOwnerGalleryAsync(Guid ownerModuleProfileId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unbounded read forbidden");
        public Task<GalleryMedia?> GetOwnedByIdAsync(Guid mediaId, Guid ownerModuleProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateTitleAsync(Guid mediaId, Guid ownerModuleProfileId, string title, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetCoverAsync(Guid mediaId, Guid ownerModuleProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
