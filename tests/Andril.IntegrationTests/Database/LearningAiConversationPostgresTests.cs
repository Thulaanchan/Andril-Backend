using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Andril.API.Integrations.AI;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>Real isolated PostgreSQL, existing policy/entities and explicit synthetic privacy
/// approval only. Does not establish production authority, migration ownership or AI dispatch.</summary>
public sealed class LearningAiConversationPostgresTests
{
    [LocalPostgresFact]
    public async Task CreationAtomicallyRoundTripsMetadataAndFirstPrivateMessage()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var repo = f.Repository(); var actor = f.Actor(); var op = Guid.NewGuid();
        var c = await repo.CreateWithUserMessageAsync(actor, op, "  PRIVATE தமிழ்  "); Assert.Equal(actor.UserId, c.AccountId); Assert.Equal(actor.ModuleProfileId, c.ModuleProfileId); Assert.Equal(AiModule.Learning, c.Module);
        Assert.Equal(f.Time.Now, c.CreatedAtUtc); Assert.Equal(c.CreatedAtUtc.AddDays(14), c.ExpiresAtUtc); Assert.Equal(c, await f.Repository().FindAsync(actor, c.ConversationId));
        var message = Assert.Single(await repo.ReadRecentMessagesAsync(actor, c.ConversationId, 100)); Assert.Equal("PRIVATE தமிழ்", message.Text); Assert.Equal(AiStoredMessageRole.User, message.Role);
        // The read DTO intentionally omits operation bindings; verify the persisted row by its returned identity.
        await using var stored = await f.BoundOwner();
        var persisted = await stored.Set<AiMessageEntity>().SingleAsync(x => x.Id == message.MessageId);
        Assert.Equal(c.ConversationId, persisted.ConversationId);
        Assert.Equal(op, persisted.OperationId);
    }
    [LocalPostgresFact]
    public async Task UserSaveRenewsExpiryButReadAndAssistantSaveNeverRenew()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var r = f.Repository(); var a = f.Actor(); var c = await r.CreateWithUserMessageAsync(a, Guid.NewGuid(), "first"); f.Time.Now = f.Time.Now.AddDays(1);
        Assert.Equal(c.ExpiresAtUtc, (await r.FindAsync(a, c.ConversationId))!.ExpiresAtUtc); var op = Guid.NewGuid(); await r.AppendUserMessageAsync(a, c.ConversationId, op, "second"); var updated = (await r.FindAsync(a, c.ConversationId))!; Assert.Equal(f.Time.Now.AddDays(14), updated.ExpiresAtUtc);
        f.Time.Now = f.Time.Now.AddHours(1); await r.AppendAssistantMessageAsync(a, c.ConversationId, op, "synthetic reply"); Assert.Equal(updated, await r.FindAsync(a, c.ConversationId));
    }
    [LocalPostgresFact]
    public async Task CreationReplayReturnsSameIdentityWithoutDuplicateMessageOrExpiryRenewal()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var r = f.Repository(); var a = f.Actor(); var op = Guid.NewGuid(); var c = await r.CreateWithUserMessageAsync(a, op, "same"); f.Time.Now = f.Time.Now.AddDays(1);
        Assert.Equal(c, await r.CreateWithUserMessageAsync(a, op, "same")); Assert.Single(await r.ReadRecentMessagesAsync(a, c.ConversationId, 100)); await Assert.ThrowsAsync<InvalidOperationException>(() => r.CreateWithUserMessageAsync(a, op, "different"));
    }
    [LocalPostgresFact]
    public async Task AppendReplayIsIdempotentAndCannotSwitchTextOrConversation()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var r = f.Repository(); var a = f.Actor(); var c = await r.CreateWithUserMessageAsync(a, Guid.NewGuid(), "first"); var other = await r.CreateWithUserMessageAsync(a, Guid.NewGuid(), "other"); var op = Guid.NewGuid();
        var m = await r.AppendUserMessageAsync(a, c.ConversationId, op, "second"); var saved = await r.FindAsync(a, c.ConversationId); f.Time.Now = f.Time.Now.AddHours(1); Assert.Equal(m, await r.AppendUserMessageAsync(a, c.ConversationId, op, "second")); Assert.Equal(saved, await r.FindAsync(a, c.ConversationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.AppendUserMessageAsync(a, other.ConversationId, op, "second")); await Assert.ThrowsAsync<InvalidOperationException>(() => r.AppendUserMessageAsync(a, c.ConversationId, op, "changed"));
    }
    [LocalPostgresFact]
    public async Task AssistantRequiresAcceptedUserOperationAndAtMostOneMatchingReply()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var r = f.Repository(); var a = f.Actor(); var op = Guid.NewGuid(); var c = await r.CreateWithUserMessageAsync(a, op, "first");
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.AppendAssistantMessageAsync(a, c.ConversationId, Guid.NewGuid(), "orphan")); var reply = await r.AppendAssistantMessageAsync(a, c.ConversationId, op, "reply"); Assert.Equal(reply, await r.AppendAssistantMessageAsync(a, c.ConversationId, op, "reply"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.AppendAssistantMessageAsync(a, c.ConversationId, op, "changed")); Assert.Equal(2, (await r.ReadRecentMessagesAsync(a, c.ConversationId, 100)).Count);
    }
    [LocalPostgresFact]
    public async Task ExistingUniversal500UnicodeElementInputRuleIsEnforcedBeforePersistence()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var r = f.Repository(); var text = string.Concat(Enumerable.Repeat("e\u0301", 500)); var c = await r.CreateWithUserMessageAsync(f.Actor(), Guid.NewGuid(), text); Assert.Equal(text, Assert.Single(await r.ReadRecentMessagesAsync(f.Actor(), c.ConversationId, 100)).Text);
        await Assert.ThrowsAsync<ArgumentException>(() => r.CreateWithUserMessageAsync(f.Actor(), Guid.NewGuid(), text + "e\u0301")); await Assert.ThrowsAsync<ArgumentException>(() => r.CreateWithUserMessageAsync(f.Actor(), Guid.Empty, "x")); Assert.Single(await r.ListAsync(f.Actor(), 100));
    }
    [LocalPostgresFact]
    public async Task UnsupportedModulesAndForgedActorBindingsCannotAccessLearningStorage()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var r = f.Repository(); var a = f.Actor(); var c = await r.CreateWithUserMessageAsync(a, Guid.NewGuid(), "keep");
        foreach (var fake in new[] { a with { Module = AiModule.Hobbies }, a with { Module = AiModule.Social }, a with { UserId = Guid.NewGuid() }, a with { ModuleProfileId = Guid.NewGuid() } })
        { Assert.Null(await r.FindAsync(fake, c.ConversationId)); Assert.Empty(await r.ReadRecentMessagesAsync(fake, c.ConversationId, 100)); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => r.AppendUserMessageAsync(fake, c.ConversationId, Guid.NewGuid(), "no")); }
    }
    [LocalPostgresFact]
    public async Task OtherAuthoritativeAccountHasIndependentConversationsAndCannotReadForeignIds()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var a = f.Actor(); var c = await f.Repository().CreateWithUserMessageAsync(a, Guid.NewGuid(), "own"); var other = new DiaryPostgresFixture.Authority(Guid.NewGuid(), Guid.NewGuid());
        await LearningPostgresFixture.Profile(f.Shared.Shared.Shared.Database, new(other.Account, other.Profiles["learning"]), other.Subject); var foreignRepo = f.Repository(other); var b = f.Actor(other); var foreign = await foreignRepo.CreateWithUserMessageAsync(b, Guid.NewGuid(), "foreign");
        Assert.Null(await foreignRepo.FindAsync(b, c.ConversationId)); Assert.Null(await f.Repository().FindAsync(a, foreign.ConversationId)); Assert.Equal(c.ConversationId, Assert.Single(await f.Repository().ListAsync(a, 100)).ConversationId); Assert.Equal(foreign.ConversationId, Assert.Single(await foreignRepo.ListAsync(b, 100)).ConversationId);
    }
    [LocalPostgresFact]
    public async Task RealInactiveLifecycleDeniesAccessAndPreservesDataUntilReactivation()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var r = f.Repository(); var a = f.Actor(); var c = await r.CreateWithUserMessageAsync(a, Guid.NewGuid(), "keep"); await f.Shared.Shared.Shared.Lifecycle("learning", false);
        Assert.Null(await r.FindAsync(a, c.ConversationId)); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => r.AppendUserMessageAsync(a, c.ConversationId, Guid.NewGuid(), "no")); await f.Shared.Shared.Shared.Lifecycle("learning", true); Assert.Equal("keep", Assert.Single(await r.ReadRecentMessagesAsync(a, c.ConversationId, 100)).Text);
    }
    [LocalPostgresFact]
    public async Task PrivacyDeniedUnavailableAndDefaultPolicyPlaceholderStayFailClosed()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var a = f.Actor(); var c = await f.Repository().CreateWithUserMessageAsync(a, Guid.NewGuid(), "keep"); f.PrivacyDenied = true; Assert.Null(await f.Repository().FindAsync(a, c.ConversationId)); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Repository().CreateWithUserMessageAsync(a, Guid.NewGuid(), "no"));
        f.PrivacyDenied = false; f.PrivacyUnavailable = true; Assert.Null(await f.Repository().FindAsync(a, c.ConversationId)); await Assert.ThrowsAsync<InvalidOperationException>(() => f.Repository().AppendUserMessageAsync(a, c.ConversationId, Guid.NewGuid(), "no")); f.PrivacyUnavailable = false;
        Assert.Null(await f.Repository(privacy: new DenyAllAiPrivacyNoticeGate()).FindAsync(a, c.ConversationId));
    }
    [LocalPostgresFact]
    public async Task RevocationDuringReadReleaseDiscardsPrivateMessageData()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var a = f.Actor(); var c = await f.Repository().CreateWithUserMessageAsync(a, Guid.NewGuid(), "PRIVATE"); var reached = false;
        var r = f.Repository(checkpoint: (phase, _) => { if (phase == "read-release") { reached = true; f.Owner.Denied = true; } return Task.CompletedTask; }); Assert.Empty(await r.ReadRecentMessagesAsync(a, c.ConversationId, 100)); Assert.True(reached); f.Owner.Denied = false; Assert.Single(await f.Repository().ReadRecentMessagesAsync(a, c.ConversationId, 100));
    }
    [LocalPostgresFact]
    public async Task ExpiryWhileAwaitingReadReleaseNeverReturnsStaleContent()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var a = f.Actor(); var c = await f.Repository().CreateWithUserMessageAsync(a, Guid.NewGuid(), "PRIVATE"); var r = f.Repository(checkpoint: (phase, _) => { if (phase == "read-release") f.Time.Now = c.ExpiresAtUtc; return Task.CompletedTask; }); Assert.Empty(await r.ReadRecentMessagesAsync(a, c.ConversationId, 100));
    }
    [LocalPostgresFact]
    public async Task ExpiredConversationCannotBeReadRevivedOrExtendedByAssistant()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var a = f.Actor(); var op = Guid.NewGuid(); var c = await f.Repository().CreateWithUserMessageAsync(a, op, "PRIVATE"); f.Time.Now = c.ExpiresAtUtc;
        Assert.Null(await f.Repository().FindAsync(a, c.ConversationId)); Assert.Empty(await f.Repository().ListAsync(a, 100)); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Repository().AppendUserMessageAsync(a, c.ConversationId, Guid.NewGuid(), "no")); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Repository().AppendAssistantMessageAsync(a, c.ConversationId, op, "no"));
    }
    [LocalPostgresFact]
    public async Task ConcurrentDistinctAppendsPreserveAllMessagesInStableServerOrder()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var a = f.Actor(); var c = await f.Repository().CreateWithUserMessageAsync(a, Guid.NewGuid(), "first");
        var results = await Task.WhenAll(f.Repository().AppendUserMessageAsync(a, c.ConversationId, Guid.NewGuid(), "a"), f.Repository().AppendUserMessageAsync(a, c.ConversationId, Guid.NewGuid(), "b")); var messages = await f.Repository().ReadRecentMessagesAsync(a, c.ConversationId, 100); Assert.Equal(3, messages.Count); Assert.Equal("first", messages[0].Text); Assert.All(results, x => Assert.Contains(messages, m => m.MessageId == x.MessageId));
    }
    [LocalPostgresFact]
    public async Task ConcurrentSameOperationCreatesOneConversationAndOneFirstMessage()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var a = f.Actor(); var op = Guid.NewGuid(); var results = await Task.WhenAll(f.Repository().CreateWithUserMessageAsync(a, op, "same"), f.Repository().CreateWithUserMessageAsync(a, op, "same")); Assert.Equal(results[0], results[1]); Assert.Single(await f.Repository().ListAsync(a, 100)); Assert.Single(await f.Repository().ReadRecentMessagesAsync(a, results[0].ConversationId, 100));
    }
    [LocalPostgresFact]
    public async Task CancellationAfterPreparedCreationRollsBackConversationAndMessage()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); using var cts = new CancellationTokenSource(); var reached = false; var r = f.Repository(checkpoint: (phase, ct) => { if (phase == "prepared") { reached = true; cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.CompletedTask; }); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.CreateWithUserMessageAsync(f.Actor(), Guid.NewGuid(), "PRIVATE", cts.Token)); Assert.True(reached);
        await using var db = await f.BoundOwner(); Assert.Empty(await db.Set<AiConversationEntity>().ToListAsync()); Assert.Empty(await db.Set<AiMessageEntity>().ToListAsync());
    }
    [LocalPostgresFact]
    public async Task InfrastructureFailureRollsBackAndExcludesSensitivePayloadFromErrors()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); const string secret = "SYNTHETIC-PRIVATE-ERROR"; var r = f.Repository(checkpoint: (phase, _) => phase == "prepared" ? Task.FromException(new IOException(secret)) : Task.CompletedTask); var error = await Assert.ThrowsAsync<InvalidOperationException>(() => r.CreateWithUserMessageAsync(f.Actor(), Guid.NewGuid(), "PRIVATE")); Assert.DoesNotContain(secret, error.ToString()); await using var db = await f.BoundOwner(); Assert.Empty(await db.Set<AiConversationEntity>().ToListAsync()); Assert.Empty(await db.Set<AiMessageEntity>().ToListAsync());
    }
    [LocalPostgresFact]
    public async Task DeletionImmediatelyHidesAndAtomicallySchedulesTheExistingPolicyDeadline()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var a = f.Actor(); var c = await f.Repository().CreateWithUserMessageAsync(a, Guid.NewGuid(), "PRIVATE"); await f.Repository().DeleteAsync(a, c.ConversationId); Assert.Null(await f.Repository().FindAsync(a, c.ConversationId)); Assert.Empty(await f.Repository().ReadRecentMessagesAsync(a, c.ConversationId, 100)); await f.Repository().DeleteAsync(a, c.ConversationId); await f.Repository().DeleteAsync(a, Guid.NewGuid());
        await using var db = await f.BoundOwner(); var entity = await db.Set<AiConversationEntity>().SingleAsync(); var work = await db.Set<IsolatedAiPurgeWork>().SingleAsync(); Assert.Equal(entity.DeletedAtUtc!.Value, work.RequestedAtUtc); Assert.Equal(AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(entity), work.PurgeByUtc);
    }
    [LocalPostgresFact]
    public async Task DeletionFailureRollsBackHiddenStateAndPurgeWorkTogether()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var a = f.Actor(); var c = await f.Repository().CreateWithUserMessageAsync(a, Guid.NewGuid(), "PRIVATE"); var r = f.Repository(checkpoint: (phase, _) => phase == "prepared" ? Task.FromException(new IOException("PRIVATE")) : Task.CompletedTask); await Assert.ThrowsAsync<InvalidOperationException>(() => r.DeleteAsync(a, c.ConversationId)); Assert.NotNull(await f.Repository().FindAsync(a, c.ConversationId)); await using var db = await f.BoundOwner(); Assert.Empty(await db.Set<IsolatedAiPurgeWork>().ToListAsync());
    }
    [LocalPostgresFact]
    public async Task RestrictedRuntimeHasForcedRlsAndCannotReadOtherModulesOrMutatePrivateBindings()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var c = await f.Repository().CreateWithUserMessageAsync(f.Actor(), Guid.NewGuid(), "PRIVATE"); await using var db = f.OpenRuntime(); await db.Database.OpenConnectionAsync(); await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{f.Owner.Subject.ToString()},false),set_config('andril.account',{f.Owner.Account.ToString()},false),set_config('andril.module','learning',false)"); Assert.Single(await db.Set<AiMessageEntity>().ToListAsync());
        foreach (var sql in new[] { "SELECT * FROM andril_learning.learning_cvs", "SELECT * FROM andril_learning.learning_portfolios", "SELECT * FROM andril_identity.accounts", "UPDATE andril_ai_isolated.messages SET text='forged'", "UPDATE andril_ai_isolated.conversations SET account_id=gen_random_uuid()", "DELETE FROM andril_ai_isolated.messages", "CREATE TABLE andril_ai_isolated.forbidden(id integer)" }) { var e = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql)); Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e.SqlState); }
        foreach (var table in new[] { "conversations", "messages", "purge_work" }) Assert.True(await db.Database.SqlQuery<bool>($"SELECT relrowsecurity AND relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid={"andril_ai_isolated." + table}::regclass").SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT NOT rolsuper AND NOT rolbypassrls AND NOT pg_has_role(current_user,'pg_signal_backend','MEMBER') AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync()); await db.Database.ExecuteSqlRawAsync("SELECT set_config('andril.account','',false),set_config('andril.subject','',false)"); Assert.Empty(await db.Set<AiMessageEntity>().ToListAsync());
    }
    [LocalPostgresFact]
    public async Task ExistingTechnicalReadBoundsRejectInvalidLimitsWithoutInventingConversationCaps()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); foreach (var limit in new[] { 0, -1, AiConversationContextPolicy.MaxCandidateMessages + 1 }) { await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Repository().ListAsync(f.Actor(), limit)); await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Repository().ReadRecentMessagesAsync(f.Actor(), Guid.NewGuid(), limit)); } Assert.Empty(await f.Repository().ListAsync(f.Actor(), 100));
    }
    [LocalPostgresFact]
    public async Task ModelUsesOnlyOwnDataForeignKeysAndCreatesNoUnapprovedMigrationHistory()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); await using var db = await f.BoundOwner(); Assert.Empty(db.Database.GetMigrations()); Assert.Equal(5, db.Model.GetEntityTypes().Count()); Assert.Contains(db.Model.GetEntityTypes(), x => x.GetTableName() == "dispatch_operations"); Assert.Contains(db.Model.GetEntityTypes(), x => x.GetTableName() == "privacy_acknowledgements"); Assert.All(db.Model.GetEntityTypes(), e => Assert.Equal(IsolatedLearningAiDbContext.Schema, e.GetSchema())); Assert.All(db.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()), fk => Assert.Equal(IsolatedLearningAiDbContext.Schema, fk.PrincipalEntityType.GetSchema()));
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_tables WHERE schemaname='andril_ai_isolated' AND tablename LIKE '%EFMigrationsHistory%') AS \"Value\"").SingleAsync());
    }
}
