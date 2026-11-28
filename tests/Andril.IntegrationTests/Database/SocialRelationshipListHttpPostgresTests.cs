using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class SocialMatchingHttpPostgresTests
{
    private const string SentLikes = Relationships + "/sent-likes";
    private const string MutualConnections = Relationships + "/mutual-connections";

    [LocalPostgresFact]
    public async Task SentLikesPaginatesOnlyOwnerPositiveDecisionsWithoutRevealingRejection()
    {
        await using var database = await OpenAsync();
        var owner = await SeedAsync(database);
        var rejected = await SeedAsync(database);
        var otherTarget = await SeedAsync(database);
        using var host = new Host(database);
        using var sender = Client(host, owner);
        using var peer = Client(host, rejected);
        using var target = Client(host, otherTarget);
        await SetPreferencesAsync(sender);
        await SetPreferencesAsync(peer);
        await SetPreferencesAsync(target);
        await Expect(await peer.PostAsJsonAsync(Interest, new { targetProfileId = owner.Profile, isPositive = false }), HttpStatusCode.OK);
        await Expect(await Like(sender, rejected), HttpStatusCode.OK);
        await Expect(await Like(sender, otherTarget), HttpStatusCode.OK);
        await Expect(await Like(target, owner), HttpStatusCode.OK);

        using var first = await sender.GetAsync(SentLikes + $"?limit=1&ownerProfileId={rejected.Profile}");
        Assert.True(first.Headers.CacheControl?.NoStore);
        var firstPage = await Json(first);
        var firstItem = Assert.Single(firstPage.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "displayName", "likedAtUtc", "profileId" }, firstItem.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var secondPage = await Json(await sender.GetAsync(SentLikes + "?limit=1&cursor=" + cursor));
        var secondItem = Assert.Single(secondPage.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { rejected.Profile, otherTarget.Profile }.Order(),
            new[] { firstItem.GetProperty("profileId").GetGuid(), secondItem.GetProperty("profileId").GetGuid() }.Order());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);

        Assert.Empty((await Json(await peer.GetAsync(SentLikes))).GetProperty("items").EnumerateArray());
        var visibleMatch = Assert.Single((await Json(await sender.GetAsync(MutualConnections))).GetProperty("items").EnumerateArray());
        Assert.Equal(otherTarget.Profile, visibleMatch.GetProperty("profileId").GetGuid());
        await Expect(await sender.GetAsync(MutualConnections + "?cursor=" + cursor), HttpStatusCode.BadRequest);
        await Expect(await sender.GetAsync(SentLikes + "?cursor=not-a-cursor"), HttpStatusCode.BadRequest);
        await Expect(await sender.GetAsync(SentLikes + "?limit=51"), HttpStatusCode.BadRequest);
        await Expect(await sender.GetAsync(SentLikes + "?limit=1&limit=2"), HttpStatusCode.BadRequest);
        using var received = await sender.GetAsync(Relationships + "/received-likes");
        Assert.Contains(received.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.DoesNotContain("GET", received.Content.Headers.Allow);
    }

    [LocalPostgresFact]
    public async Task BothRelationshipListsRequireConfirmedActiveSocialIdentity()
    {
        await using var database = await OpenAsync();
        var active = await SeedAsync(database);
        var adultInactive = await SeedAsync(database, active: false);
        var minor = await SeedAsync(database, minor: true);
        var hobbies = await SeedAsync(database, module: "hobbies");
        using var host = new Host(database);
        using var anonymous = Client(host);
        using var unconfirmed = Client(host, active, confirmed: false);
        using var inactive = Client(host, adultInactive);
        using var underage = Client(host, minor);
        using var otherModule = Client(host, hobbies);
        foreach (var path in new[] { SentLikes, MutualConnections })
        {
            await Expect(await anonymous.GetAsync(path), HttpStatusCode.Unauthorized);
            await Expect(await unconfirmed.GetAsync(path), HttpStatusCode.Forbidden);
            await Expect(await inactive.GetAsync(path), HttpStatusCode.Forbidden);
            await Expect(await underage.GetAsync(path), HttpStatusCode.Forbidden);
            await Expect(await otherModule.GetAsync(path), HttpStatusCode.Forbidden);
        }
        using var disabled = new Host(database, enabled: false);
        using var disabledClient = Client(disabled, active);
        await Expect(await disabledClient.GetAsync(SentLikes), HttpStatusCode.NotFound);
        await Expect(await disabledClient.GetAsync(MutualConnections), HttpStatusCode.NotFound);
    }

    [LocalPostgresFact]
    public async Task MutualConnectionsExposeOnlyCommittedScopedMatchesAndSuppressRevocation()
    {
        await using var database = await OpenAsync();
        var owner = await SeedAsync(database);
        var peer = await SeedAsync(database);
        var outsider = await SeedAsync(database);
        using var host = new Host(database);
        using var sender = Client(host, owner);
        using var other = Client(host, peer);
        using var third = Client(host, outsider);
        await SetPreferencesAsync(sender);
        await SetPreferencesAsync(other);
        await SetPreferencesAsync(third);
        await Expect(await Like(sender, peer), HttpStatusCode.OK);
        Assert.Empty((await Json(await sender.GetAsync(MutualConnections))).GetProperty("items").EnumerateArray());
        var match = await Json(await Like(other, owner));
        var matchId = match.GetProperty("matchId").GetGuid();
        var active = await Json(await sender.GetAsync(MutualConnections));
        var connection = Assert.Single(active.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "displayName", "matchId", "matchedAtUtc", "profileId" }, connection.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(peer.Profile, connection.GetProperty("profileId").GetGuid());
        Assert.Equal(matchId, connection.GetProperty("matchId").GetGuid());
        Assert.Empty((await Json(await third.GetAsync(MutualConnections + $"?ownerProfileId={owner.Profile}"))).GetProperty("items").EnumerateArray());
        await Expect(await sender.PostAsync($"{Relationships}/{peer.Profile}/revoke", null), HttpStatusCode.OK);
        Assert.Empty((await Json(await sender.GetAsync(MutualConnections))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Json(await other.GetAsync(MutualConnections))).GetProperty("items").EnumerateArray());
    }

    [LocalPostgresFact]
    public async Task RelationshipListsWaitForBlockCommitAndCancellationReleasesTheRead()
    {
        await using var database = await OpenAsync();
        var owner = await SeedAsync(database);
        var peer = await SeedAsync(database);
        using var host = new Host(database);
        using var sender = Client(host, owner);
        using var blocker = Client(host, peer);
        await SetPreferencesAsync(sender);
        await SetPreferencesAsync(blocker);
        await Expect(await Like(sender, peer), HttpStatusCode.OK);
        await Expect(await Like(blocker, owner), HttpStatusCode.OK);
        var intercepted = new ListLockObserved();
        using var readHost = new Host(database, pause: intercepted);
        using var reader = Client(readHost, owner);
        var secondIntercept = new ListLockObserved();
        using var secondReadHost = new Host(database, pause: secondIntercept);
        using var secondReader = Client(secondReadHost, owner);

        await using var writer = database.Database.Open();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT set_config('andril.subject', {peer.Subject.ToString()}, true),
                set_config('andril.account', {peer.Account.ToString()}, true), set_config('andril.module', 'social', true);
            SELECT andril_social.set_social_block({owner.Profile}, true)
            """);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancelledRead = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var queued = reader.GetAsync(MutualConnections, cancelledRead.Token);
        await intercepted.Entered.Task.WaitAsync(deadline.Token);
        Assert.False(queued.IsCompleted);
        await cancelledRead.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        var fresh = secondReader.GetAsync(MutualConnections, deadline.Token);
        await secondIntercept.Entered.Task.WaitAsync(deadline.Token);
        Assert.False(fresh.IsCompleted);
        await transaction.CommitAsync(deadline.Token);
        Assert.Empty((await Json(await fresh)).GetProperty("items").EnumerateArray());
        Assert.Empty((await Json(await sender.GetAsync(SentLikes))).GetProperty("items").EnumerateArray());
        await Expect(await blocker.DeleteAsync($"{Social}/safety/blocks/{owner.Profile}"), HttpStatusCode.NoContent);
        Assert.Empty((await Json(await sender.GetAsync(MutualConnections))).GetProperty("items").EnumerateArray());
    }

    [LocalPostgresFact]
    public async Task RelationshipListsSuppressRestrictedAndDeactivatedPeers()
    {
        await using var database = await OpenAsync();
        var owner = await SeedAsync(database);
        var target = await SeedAsync(database);
        using var host = new Host(database);
        using var sender = Client(host, owner);
        using var peer = Client(host, target);
        await SetPreferencesAsync(sender);
        await SetPreferencesAsync(peer);
        await Expect(await Like(sender, target), HttpStatusCode.OK);
        await Expect(await Like(peer, owner), HttpStatusCode.OK);
        await using (var social = database.Database.Open())
            await social.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_social.social_moderation_grants(account_id) VALUES ({owner.Account})");
        var restriction = $"{Social}/safety/restrictions/{target.Profile}";
        await Expect(await sender.PutAsJsonAsync(restriction, new { restricted = true }), HttpStatusCode.NoContent);
        Assert.Empty((await Json(await sender.GetAsync(SentLikes))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Json(await sender.GetAsync(MutualConnections))).GetProperty("items").EnumerateArray());
        await Expect(await sender.PutAsJsonAsync(restriction, new { restricted = false }), HttpStatusCode.NoContent);
        await using (var accounts = database.Database.OpenAccounts())
            await accounts.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE andril_identity.account_module_lifecycles SET state = 'inactive', revision = revision + 1,
                    state_changed_at_utc = CURRENT_TIMESTAMP WHERE account_id = {target.Account} AND module = 'social'
                """);
        Assert.Empty((await Json(await sender.GetAsync(SentLikes))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Json(await sender.GetAsync(MutualConnections))).GetProperty("items").EnumerateArray());
    }

    private sealed class ListLockObserved : DbCommandInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText == "SELECT andril_social.social_safety_lock()") Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
