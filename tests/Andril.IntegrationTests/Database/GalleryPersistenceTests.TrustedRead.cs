using System.Text.Json;
using Andril.API.Authorization.Hobbies;
using Andril.Common.Accounts;
using Andril.Hobbies.DTOs;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class GalleryPersistenceTests
{
    [Fact]
    public async Task TrustedRead_ReauthorizesEveryPageAndRejectsForeignPositions()
    {
        var authorization = new TestGalleryAuthorization();
        var repository = new TestGalleryReadRepository();
        var boundary = authorization.CreateBoundary(repository, repository);
        var first = await boundary.GetPageAsync(1);
        Assert.NotNull(first.Next);

        authorization.ProfileId = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => boundary.GetPageAsync(1, first.Next));
        authorization.ProfileId = OtherOwner;
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.GetPageAsync(1, first.Next));
        authorization.ProfileId = Owner;
        authorization.Allowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => boundary.GetPageAsync(1, first.Next));
        authorization.Allowed = true;
        await boundary.GetPageAsync(1, first.Next);

        Assert.Equal(5, authorization.AccountLookups);
        Assert.Equal(5, authorization.ProfileLookups);
        Assert.Equal(4, authorization.OperationLookups);
        Assert.Equal(2, repository.PageReads);
    }

    [Fact]
    public async Task TrustedRead_SanitizesDependencyFailuresAndCancelsBeforeReading()
    {
        var authorization = new TestGalleryAuthorization { AccountUnavailable = true };
        var repository = new TestGalleryReadRepository();
        var boundary = authorization.CreateBoundary(repository, repository);
        var denial = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => boundary.GetByIdAsync(Newer));
        Assert.Null(denial.InnerException);
        Assert.Equal("An active Hobbies profile is required.", denial.Message);
        Assert.Equal(0, repository.DetailReads);

        authorization.AccountUnavailable = false;
        repository.DetailUnavailable = true;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.GetByIdAsync(Newer));
        Assert.Equal("Gallery retrieval is unavailable.", failure.Message);
        Assert.Null(failure.InnerException);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => boundary.GetByIdAsync(Newer, cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.Equal(1, repository.DetailReads);
        Assert.Equal(2, authorization.AccountLookups);
    }

    [Fact]
    public async Task TrustedRead_PrivateEvidenceRequiresExactObjectAndUniformlyDeniesMissingEvidence()
    {
        var authorization = new TestGalleryAuthorization();
        var repository = new TestGalleryReadRepository { Media = NewPrivateMedia() };
        var boundary = authorization.CreateBoundary(repository, repository);
        var objects = new TestPrivateObjects();

        Assert.True(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        Assert.Equal(GalleryReadOperation.PrivateObjectEvidence, authorization.LastOperation);
        var expected = Assert.IsType<GalleryPrivateObjectEvidence>(objects.LastExpected);
        Assert.Equal(repository.Media.StorageObjectKey, expected.StorageObjectKey);
        Assert.Equal(Owner, expected.OwnerModuleProfileId);
        Assert.Equal("Hobbies", expected.Module);
        Assert.Equal(Newer, expected.MediaId);

        objects.LastExpected = null;
        authorization.Allowed = false;
        Assert.False(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        authorization.Allowed = true;
        authorization.ProfileId = null;
        Assert.False(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        Assert.Null(objects.LastExpected);
        authorization.ProfileId = Owner;

        foreach (var response in new GalleryPrivateObjectEvidence?[]
        {
            null, expected with { OwnerModuleProfileId = OtherOwner }, expected with { Module = "Learning" },
            expected with { MediaId = Older }, expected with { StorageObjectKey = "different-object" }
        })
        {
            objects.Reply = _ => response;
            Assert.False(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        }

        objects.Reply = _ => throw new InvalidOperationException("synthetic provider details");
        Assert.False(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        objects.LastExpected = null;
        Assert.False(await boundary.VerifyPrivateMediaAsync(Older, objects));
        repository.Media.StorageObjectKey = " ";
        Assert.False(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        repository.Media = NewPrivateMedia();
        repository.Media.OwnerModuleProfileId = OtherOwner;
        Assert.False(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        repository.Media.OwnerModuleProfileId = Owner;
        repository.Media.Module = "Learning";
        Assert.False(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        repository.Media.Module = "Hobbies";
        repository.Media.IsDeleted = true;
        Assert.False(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        repository.Media.IsDeleted = false;
        repository.Media.IsPrivate = false;
        Assert.False(await boundary.VerifyPrivateMediaAsync(Newer, objects));
        Assert.Null(objects.LastExpected);
    }

    [MapCapturePostgresFact]
    public Task TrustedRead_UsesScopedPostgresRowsAndReauthorizesContinuation() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, [.. KeysetRows(), KeysetMedia(10, CreatedAt, OtherOwner),
            KeysetMedia(11, CreatedAt, module: "Learning"), KeysetMedia(12, CreatedAt, deleted: true)]);
        await using var context = CreateContext(connection);
        var repository = new GalleryRepository<HobbiesGalleryDbContext>(context);
        var authorization = new TestGalleryAuthorization();
        var boundary = authorization.CreateBoundary(repository, repository);
        var first = await boundary.GetPageAsync(2);
        Assert.Equal(new[] { KeysetId(1), KeysetId(2) }, first.Items.Select(item => item.Id));
        Assert.NotNull(first.Next);
        var objects = new TestPrivateObjects();
        foreach (var inaccessible in new[] { 10, 11, 12, 99 })
        {
            Assert.Null(await boundary.GetByIdAsync(KeysetId(inaccessible)));
            Assert.False(await boundary.VerifyPrivateMediaAsync(KeysetId(inaccessible), objects));
        }
        Assert.Null(objects.LastExpected);
        var owned = Assert.IsType<GalleryPhotoDto>(await boundary.GetByIdAsync(KeysetId(1)));
        var json = JsonSerializer.Serialize(owned);
        Assert.DoesNotContain("StorageObjectKey", json);
        Assert.DoesNotContain(Owner.ToString(), json);

        authorization.ProfileId = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => boundary.GetPageAsync(2, first.Next));
        authorization.ProfileId = Owner;
        var second = await boundary.GetPageAsync(2, first.Next);
        Assert.Equal(new[] { KeysetId(3), KeysetId(4) }, second.Items.Select(item => item.Id));
    });

    private static GalleryMedia NewPrivateMedia() => new()
    {
        Id = Newer, OwnerModuleProfileId = Owner, StorageObjectKey = "synthetic/private.jpg"
    };

    // These authorities exist only in the test assembly.
    private sealed class TestGalleryAuthorization : ICurrentUser, IAccountRepository, IHobbiesGalleryProfileAccessReader
    {
        private readonly Account _account = Account.Create(Guid.NewGuid(), CreatedAt);
        public Guid? UserId => _account.SupabaseUserId;
        public bool IsAuthenticated => true;
        public Guid? ProfileId { get; set; } = Owner;
        public bool Allowed { get; set; } = true;
        public bool AccountUnavailable { get; set; }
        public bool AccountMissing { get; set; }
        public int AccountLookups { get; private set; }
        public int ProfileLookups { get; private set; }
        public int OperationLookups { get; private set; }
        public GalleryReadOperation LastOperation { get; private set; }

        public GalleryReadBoundary CreateBoundary(IGalleryRepository repository, IGalleryPageRepository pages,
            ICurrentUser? currentUser = null) =>
            new(new AccountBackedGalleryActorResolver(currentUser ?? this, this, this), AuthorizeAsync, repository, pages, 3);

        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Assert.Equal(UserId, subject);
            AccountLookups++;
            if (AccountUnavailable)
                throw new InvalidOperationException("synthetic account provider details");
            return Task.FromResult(AccountMissing ? null : _account);
        }

        public Task<Guid?> FindActiveOwnedHobbiesProfileIdAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_account.Id, accountId);
            ProfileLookups++;
            return Task.FromResult(ProfileId);
        }

        private Task<bool> AuthorizeAsync(GalleryActor actor, GalleryReadOperation operation, CancellationToken token)
        {
            Assert.Equal(ProfileId, actor.ModuleProfileId);
            Assert.Equal("Hobbies", actor.Module);
            Assert.True(actor.IsActive);
            OperationLookups++;
            LastOperation = operation;
            return Task.FromResult(Allowed);
        }

        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestPrivateObjects : IGalleryPrivateObjectEvidenceReader
    {
        public Func<GalleryPrivateObjectEvidence, GalleryPrivateObjectEvidence?> Reply { get; set; } = evidence => evidence;
        public GalleryPrivateObjectEvidence? LastExpected { get; set; }

        public Task<GalleryPrivateObjectEvidence?> FindAuthorizedAsync(GalleryPrivateObjectEvidence expected, CancellationToken token)
        {
            LastExpected = expected;
            return Task.FromResult(Reply(expected));
        }
    }

    private sealed class TestGalleryReadRepository : IGalleryRepository, IGalleryPageRepository
    {
        public int PageReads { get; private set; }
        public int DetailReads { get; private set; }
        public GalleryMedia? Media { get; set; }
        public bool DetailUnavailable { get; set; }
        public Action? DuringDetail { get; set; }
        public Action? DuringPage { get; set; }
        public bool FinalPage { get; set; }
        public int LastPageSize { get; private set; }

        public Task<GalleryMedia?> GetOwnedByIdAsync(Guid mediaId, Guid ownerModuleProfileId, CancellationToken token)
        {
            Assert.Equal(Owner, ownerModuleProfileId);
            DetailReads++;
            if (DetailUnavailable)
                throw new InvalidOperationException("synthetic database details");
            DuringDetail?.Invoke();
            return Task.FromResult(Media);
        }

        public Task<GalleryPage> GetOwnerGalleryPageAsync(Guid ownerId, int size, int maximum,
            GalleryPagePosition? after, CancellationToken token)
        {
            Assert.Equal(Owner, ownerId);
            Assert.Equal(3, maximum);
            PageReads++;
            LastPageSize = size;
            DuringPage?.Invoke();
            if (FinalPage)
                return Task.FromResult(new GalleryPage([], null));
            return Task.FromResult(new GalleryPage([NewPrivateMedia()], new(ownerId, "Hobbies", CreatedAt, Newer)));
        }

        public Task<Guid?> GetCoverIdAsync(Guid ownerModuleProfileId, CancellationToken token) =>
            Task.FromResult<Guid?>(null);

        public Task<IReadOnlyList<GalleryMedia>> GetOwnerGalleryAsync(Guid ownerModuleProfileId, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<bool> UpdateTitleAsync(Guid mediaId, Guid ownerModuleProfileId, string title, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<bool> SetCoverAsync(Guid mediaId, Guid ownerModuleProfileId, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
