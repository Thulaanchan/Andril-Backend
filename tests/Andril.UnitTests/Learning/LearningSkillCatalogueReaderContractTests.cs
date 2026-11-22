using System.Text.Json;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Xunit;
using Skill = Andril.Learning.Entities.LearningSkillCatalogue.Skill;

namespace Andril.UnitTests.Learning;

/// <summary>
/// Structural result and controlled fake-reader contract tests only. No production loading,
/// authoritative freshness, database authorization or onboarding completion is established.
/// </summary>
public sealed class LearningSkillCatalogueReaderContractTests
{
    private static LearningSkillCatalogue Catalogue(Guid id) =>
        new([new Skill(id, true, LearningSkillCatalogueStatus.Active)]);

    [Fact]
    public void AvailableResult_RetainsCatalogueAndOpaqueRevision()
    {
        var id = Guid.NewGuid();
        var catalogue = Catalogue(id);
        var result = LearningSkillCatalogueReadResult.Available(catalogue, "opaque-revision");
        Assert.Equal(LearningSkillCatalogueReadState.Available, result.State);
        Assert.Same(catalogue, result.Catalogue);
        Assert.Equal("opaque-revision", result.Revision);
        result.Catalogue!.ValidateNewSelections([id], [id]);
    }

    [Fact]
    public void MissingSuccessfulSnapshot_IsRejected() =>
        Assert.Throws<ArgumentNullException>("catalogue", () =>
            LearningSkillCatalogueReadResult.Available(null!, "opaque-revision"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void MissingSuccessfulRevision_IsRejected(string? revision) =>
        Assert.Throws<ArgumentException>("revision", () =>
            LearningSkillCatalogueReadResult.Available(Catalogue(Guid.NewGuid()), revision!));

    [Theory]
    [InlineData("opaque:not-a-number")]
    [InlineData("  opaque revision  ")]
    [InlineData("தமிழ்-සිංහල-e\u0301")]
    public void OpaqueRevision_IsPreservedWithoutParsingTrimmingOrNormalization(string revision)
    {
        var result = LearningSkillCatalogueReadResult.Available(Catalogue(Guid.NewGuid()), revision);
        Assert.Equal(revision, result.Revision);
    }

    [Fact]
    public void Unavailable_HasNoSnapshotRevisionOrSyntheticFallback()
    {
        var result = LearningSkillCatalogueReadResult.Unavailable();
        Assert.Equal(LearningSkillCatalogueReadState.Unavailable, result.State);
        Assert.Null(result.Catalogue);
        Assert.Null(result.Revision);
    }

    [Fact]
    public void VerifiedEmptySnapshot_IsStructurallyDifferentFromUnavailable()
    {
        // The factory does not verify authority: this empty snapshot is controlled test data.
        var available = LearningSkillCatalogueReadResult.Available(new LearningSkillCatalogue([]), "empty-revision");
        var unavailable = LearningSkillCatalogueReadResult.Unavailable();
        Assert.Equal(LearningSkillCatalogueReadState.Available, available.State);
        Assert.NotNull(available.Catalogue);
        available.Catalogue!.ValidateNewSelections([], []);
        Assert.Throws<ArgumentException>(() => available.Catalogue.ValidateNewSelections([Guid.NewGuid()], []));
        Assert.Equal(LearningSkillCatalogueReadState.Unavailable, unavailable.State);
        Assert.Null(unavailable.Catalogue);
    }

    [Fact]
    public void SuccessfulResult_RetainsDefensiveCatalogueSnapshot()
    {
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var entries = new List<Skill> { new(id, true, LearningSkillCatalogueStatus.Active) };
        var result = LearningSkillCatalogueReadResult.Available(new LearningSkillCatalogue(entries), "revision");
        entries[0] = new(id, true, LearningSkillCatalogueStatus.Merged);
        entries.Add(new(otherId, true, LearningSkillCatalogueStatus.Active));
        entries.Clear();
        result.Catalogue!.ValidateNewSelections([id], [id]);
        Assert.Throws<ArgumentException>(() => result.Catalogue.ValidateNewSelections([otherId], []));
        Assert.Equal("revision", result.Revision);
        Assert.All(typeof(LearningSkillCatalogueReadResult).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void RejectedConstruction_DoesNotChangeAnExistingSuccessfulResult()
    {
        var id = Guid.NewGuid();
        var catalogue = Catalogue(id);
        var result = LearningSkillCatalogueReadResult.Available(catalogue, "original");
        Assert.Throws<ArgumentException>(() => LearningSkillCatalogueReadResult.Available(catalogue, ""));
        var unavailable = LearningSkillCatalogueReadResult.Unavailable();
        Assert.Equal(LearningSkillCatalogueReadState.Available, result.State);
        Assert.Same(catalogue, result.Catalogue);
        Assert.Equal("original", result.Revision);
        result.Catalogue!.ValidateNewSelections([id], [id]);
        Assert.Null(unavailable.Catalogue);
    }

    [Fact]
    public async Task FakeReader_ReturnsSuppliedSnapshotWithoutClaimingAuthority()
    {
        var expected = LearningSkillCatalogueReadResult.Available(Catalogue(Guid.NewGuid()), "revision");
        using var cts = new CancellationTokenSource();
        var reader = new ContractFake(token =>
        {
            Assert.Equal(cts.Token, token);
            return Task.FromResult(expected);
        });
        Assert.Same(expected, await reader.ReadCurrentAsync(cts.Token));
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task FakeReader_UnavailableReadDoesNotInventSuccessOrRetry()
    {
        var unavailable = LearningSkillCatalogueReadResult.Unavailable();
        var reader = new ContractFake(_ => Task.FromResult(unavailable));
        var result = await reader.ReadCurrentAsync();
        Assert.Same(unavailable, result);
        Assert.Equal(LearningSkillCatalogueReadState.Unavailable, result.State);
        Assert.Null(result.Catalogue);
        Assert.Null(result.Revision);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task FakeReader_PreCancelledReadDoesNotRunItsReadOperation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = new ContractFake(_ => Task.FromResult(LearningSkillCatalogueReadResult.Unavailable()));
        var task = reader.ReadCurrentAsync(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FakeReader_CancellationAfterReadNeverReturnsAvailableOrUnavailable(bool available)
    {
        using var cts = new CancellationTokenSource();
        var supplied = available
            ? LearningSkillCatalogueReadResult.Available(Catalogue(Guid.NewGuid()), "revision")
            : LearningSkillCatalogueReadResult.Unavailable();
        var reader = new ContractFake(_ => { cts.Cancel(); return Task.FromResult(supplied); });
        var task = reader.ReadCurrentAsync(cts.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.True(task.IsCanceled);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task FakeReader_CancellationDuringPendingReadRemainsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var pending = new TaskCompletionSource<LearningSkillCatalogueReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new ContractFake(token => pending.Task.WaitAsync(token));
        var task = reader.ReadCurrentAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
        Assert.Equal(1, reader.Calls);
        Assert.False(pending.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultJsonAndDiagnostics_DoNotExposeRevisionOrCatalogueFacts(bool available)
    {
        var id = Guid.NewGuid();
        var revision = "synthetic-private-revision";
        var result = available ? LearningSkillCatalogueReadResult.Available(Catalogue(id), revision)
            : LearningSkillCatalogueReadResult.Unavailable();
        var json = JsonSerializer.Serialize((object)result, result.GetType(), new JsonSerializerOptions());
        Assert.True(json == "{}", "Default JSON must omit internal reader evidence.");
        Assert.True(!result.ToString().Contains(revision, StringComparison.Ordinal), "Diagnostics must omit revision evidence.");
        Assert.True(!result.ToString().Contains(id.ToString(), StringComparison.Ordinal), "Diagnostics must omit catalogue identities.");
        Assert.Equal(available ? LearningSkillCatalogueReadState.Available : LearningSkillCatalogueReadState.Unavailable, result.State);
    }

    // This fake demonstrates required consumer-visible semantics; no production reader exists.
    private sealed class ContractFake(Func<CancellationToken, Task<LearningSkillCatalogueReadResult>> read)
        : ILearningSkillCatalogueReader
    {
        internal int Calls;
        public async Task<LearningSkillCatalogueReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var result = await read(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }
}
