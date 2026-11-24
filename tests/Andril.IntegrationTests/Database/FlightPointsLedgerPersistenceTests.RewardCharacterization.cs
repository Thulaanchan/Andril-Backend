using Andril.Economy.FlightPoints;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class FlightPointsLedgerPersistenceTests
{
    // These passing CHARACTERIZATION tests expose integration gaps in the existing schema.
    // They do not assert that duplicate events, arbitrary SQL mutation or inconsistent balances are safe.
    // T04/T05 event uniqueness, T14 database consistency, and T16 restricted-role immutability remain BLOCKED.
    [MapCapturePostgresFact]
    public Task RewardCharacterization_T04_SameEventKeyWithFreshEntryIdIsNotDeduplicated() => WithDatabaseAsync(async connection =>
    {
        var first = EventGrant(FirstId); var repeated = EventGrant(SecondId);
        await using var writer = CreateContext(connection);
        await Repository(writer).AppendAsync([first]);
        await Repository(writer).AppendAsync([repeated]);
        await using var reader = CreateContext(connection);
        var rows = await reader.FlightPointsLedgerEntries.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal(first.IdempotencyKey, row.IdempotencyKey));
        Assert.Equal(2L * FlightPointsRewardPolicy.FirstEligibleContributionPoints, await Repository(reader).GetNetPointsAsync(Owner));
    });

    [MapCapturePostgresFact]
    public Task RewardCharacterization_T05_ConcurrentSameEventWithFreshIdsCommitsTwice() => WithDatabaseAsync(async connection =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var gate = new SaveGate(); // Existing deterministic barrier: both contexts reach SaveChanges before either inserts.
        await using var first = CreateContext(connection, interceptor: gate);
        await using var second = CreateContext(connection, interceptor: gate);
        await Task.WhenAll(Repository(first).AppendAsync([EventGrant(FirstId)], deadline.Token),
            Repository(second).AppendAsync([EventGrant(SecondId)], deadline.Token));
        await using var reader = CreateContext(connection);
        Assert.Equal(2, await reader.FlightPointsLedgerEntries.CountAsync());
        Assert.Equal(2L * FlightPointsRewardPolicy.FirstEligibleContributionPoints, await Repository(reader).GetNetPointsAsync(Owner));
        Assert.Empty(first.ChangeTracker.Entries()); Assert.Empty(second.ChangeTracker.Entries());
    });

    [MapCapturePostgresFact]
    public Task RewardCharacterization_T14_SignedTotalDoesNotHideRawCrossOwnerReversalCorruption() => WithDatabaseAsync(async connection =>
    {
        await using var writer = CreateContext(connection);
        var foreignGrant = Grant(FirstId, owner: OtherOwner);
        await Repository(writer).AppendAsync([foreignGrant]);
        // A direct SQL fixture bypasses ValidateReversalsAsync. No new DDL or business rule is introduced.
        await InsertRowAsync(writer, new(SecondId, Owner, Contribution, Attraction,
            (int)FlightPointsLedgerEntryType.Reversal, (int)FlightPointsRewardType.FirstEligibleContribution,
            -FlightPointsRewardPolicy.FirstEligibleContributionPoints, "synthetic-corrupt-reversal", FirstId));
        await using var reader = CreateContext(connection);
        Assert.Equal(-(long)FlightPointsRewardPolicy.FirstEligibleContributionPoints, await Repository(reader).GetNetPointsAsync(Owner));
        Assert.Equal((long)FlightPointsRewardPolicy.FirstEligibleContributionPoints, await Repository(reader).GetNetPointsAsync(OtherOwner));
        Assert.Equal(2, await reader.FlightPointsLedgerEntries.CountAsync());
        // The signed query reports the inconsistency; it is not a nonnegative-balance policy or authorization boundary.
    });

    [MapCapturePostgresFact]
    public Task RewardCharacterization_T16_AppendOnlyRepositoryDoesNotPreventTestOwnerSqlMutation() => WithDatabaseAsync(async connection =>
    {
        await using var writer = CreateContext(connection);
        var grant = EventGrant(FirstId);
        await Repository(writer).AppendAsync([grant]);
        const string changed = "synthetic-direct-sql-key-change";
        Assert.Equal(1, await writer.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_hobbies.flight_point_ledger SET idempotency_key={changed} WHERE ledger_entry_id={grant.LedgerEntryId}
            """));
        await using var reader = CreateContext(connection);
        var persisted = await Repository(reader).FindOwnedAsync(grant.LedgerEntryId, Owner);
        Assert.NotNull(persisted); Assert.Equal(changed, persisted.IdempotencyKey);
        Assert.Equal("synthetic-same-approved-event", grant.IdempotencyKey);
        Assert.Equal(grant.PointsDelta, persisted.PointsDelta);
        // This uses the disposable table owner, not a proposed production runtime role or public API.
    });

    private static FlightPointsLedgerEntry EventGrant(Guid id) => FlightPointsLedgerEntry.CreateGrant(id, Owner,
        Contribution, Attraction, FlightPointsRewardType.FirstEligibleContribution, "synthetic-same-approved-event", CreatedAt);
}
