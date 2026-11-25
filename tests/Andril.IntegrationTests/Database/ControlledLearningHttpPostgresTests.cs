using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Andril.API.Controllers.Learning;
using Andril.Learning.Entities;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>B2-10: real signed-bearer HTTP, application, restricted PostgreSQL runtime and
/// independent committed-state checks. No production M2 standing/catalogue authority is claimed.</summary>
public sealed class ControlledLearningHttpPostgresTests
{
    private static LearningSetupRevision Revision(LearningSetupResponse response)
    { Assert.True(LearningSetupRevision.TryParse(response.Revision, out var r)); return r!; }

    [LocalPostgresFact]
    public async Task SignedOwnerCanReadInitialSetupBeforeCompletionAndPersistOverlapAcrossFreshRequests()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        Assert.Equal(0, Revision(before).Version); Assert.Empty(before.Goals); Assert.Empty(before.TeachingSkills);
        var response = await h.Select(before.Revision, [h.Skill], [h.Skill]); Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var after = await h.Read(); Assert.Equal(h.Skill, Assert.Single(after.TeachingSkills)); Assert.Equal(h.Skill, Assert.Single(after.LearningSkills)); Assert.Equal(1, Revision(after).Version);
        await using var owner = LearningPostgresFixture.Open(h.Fixture); Assert.Equal(2, await owner.SkillSelections.CountAsync());
        Assert.Equal(after.Revision, LearningSetupRevision.FromPersisted((await owner.Set<LearningProfileRevisionRow>().SingleAsync()).GenerationId, (await owner.Set<LearningProfileRevisionRow>().SingleAsync()).Version).ToOpaqueToken());
    }

    [LocalPostgresFact]
    public async Task MissingInvalidExpiredWrongIssuerAndWrongSignatureTokensCannotReadOrWrite()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        foreach (var token in new string?[] { null, "invalid", h.Token(expired: true), h.Token(issuer: "foreign"), h.Token(wrongSignature: true) })
        {
            h.Client.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await h.Select(before.Revision, [h.Skill], [])).StatusCode);
        }
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", h.Token()); Assert.Equal(before.Revision, (await h.Read()).Revision);
        await using var db = LearningPostgresFixture.Open(h.Fixture); Assert.Empty(await db.SkillSelections.ToListAsync()); Assert.Empty(await db.Set<LearningMutationReceiptRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task ForeignSignedSubjectAndAnonymousIdentityCannotUseAnotherAccountProfile()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        foreach (var token in new[] { h.Token(subject: Guid.NewGuid()), h.Token(anonymous: true) })
        {
            h.Client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var read = await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root); Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            var body = await read.Content.ReadAsStringAsync(); Assert.DoesNotContain(h.Profile.ToString(), body); Assert.DoesNotContain(h.Account.ToString(), body);
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Select(before.Revision, [h.Skill], [])).StatusCode);
        }
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", h.Token()); Assert.Equal(before.Revision, (await h.Read()).Revision);
    }

    [LocalPostgresFact]
    public async Task WrongModuleRestrictedAndRevokedEvidenceCannotDiscloseOrMutate()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        for (var i = 0; i < 3; i++)
        {
            h.WrongModule = i == 0; h.Restricted = i == 1; h.Revoked = i == 2;
            var read = await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root); Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            Assert.DoesNotContain(before.Revision, await read.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Select(before.Revision, [h.Skill], [])).StatusCode);
        }
        h.WrongModule = h.Restricted = h.Revoked = false; Assert.Equal(before.Revision, (await h.Read()).Revision);
    }

    [LocalPostgresFact]
    public async Task DeactivationDeniesHttpWhilePreservingRowsAndReactivationPreservesGeneration()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        Assert.Equal(HttpStatusCode.NoContent, (await h.Select(before.Revision, [h.Skill], [])).StatusCode); var saved = await h.Read();
        await h.OwnerSql($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1 WHERE account_id={h.Account} AND module='learning'");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Select(saved.Revision, [], [])).StatusCode);
        await using (var db = LearningPostgresFixture.Open(h.Fixture)) Assert.Single(await db.SkillSelections.ToListAsync());
        await h.OwnerSql($"UPDATE andril_identity.account_module_lifecycles SET state='active',revision=revision+1 WHERE account_id={h.Account} AND module='learning'");
        Assert.Equal(saved.Revision, (await h.Read()).Revision);
    }

    [LocalPostgresFact]
    public async Task GoalCreateEditNoOpAndRemovalUseActualRestrictedRuntimeAndKeepSelectionIdentities()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        Assert.Equal(HttpStatusCode.NoContent, (await h.Select(before.Revision, [h.Skill], [])).StatusCode); var selected = await h.Read();
        await using var owner = LearningPostgresFixture.Open(h.Fixture); var selection = (await owner.SkillSelections.AsNoTracking().SingleAsync()).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send(HttpMethod.Post, "/goals", new { expectedRevision = selected.Revision, text = "<script>SYNTHETIC-GOAL</script>" })).StatusCode);
        var added = await h.Read(); var goal = Assert.Single(added.Goals); Assert.Equal("<script>SYNTHETIC-GOAL</script>", goal.Text); Assert.Equal(Revision(selected).Version + 1, Revision(added).Version);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send(HttpMethod.Put, "/goals/" + goal.GoalId, new { expectedRevision = added.Revision, text = goal.Text })).StatusCode);
        Assert.Equal(added.Revision, (await h.Read()).Revision);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send(HttpMethod.Put, "/goals/" + goal.GoalId, new { expectedRevision = added.Revision, text = "SYNTHETIC-EDITED" })).StatusCode);
        var edited = await h.Read(); Assert.Equal(goal.GoalId, Assert.Single(edited.Goals).GoalId);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Send(HttpMethod.Delete, "/goals/" + goal.GoalId, new { expectedRevision = edited.Revision })).StatusCode);
        var removed = await h.Read(); Assert.Empty(removed.Goals); Assert.Equal(selection, (await owner.SkillSelections.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(Revision(edited).Version + 1, Revision(removed).Version);
    }

    [LocalPostgresFact]
    public async Task InvalidCatalogueStaleLegacyAndWrongGenerationCannotCommitOrRefresh()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Select(before.Revision, [Guid.NewGuid()], [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Select(new string('A', 64), [h.Skill], [])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Select(LearningSetupRevision.FromPersisted(Guid.NewGuid(), 0).ToOpaqueToken(), [h.Skill], [])).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Select(before.Revision, [h.Skill], [])).StatusCode); var current = await h.Read();
        Assert.Equal(HttpStatusCode.Conflict, (await h.Select(before.Revision, [h.Skill], [])).StatusCode); // stale even identical content
        Assert.Equal(HttpStatusCode.Conflict, (await h.Select(before.Revision, [h.OtherSkill], [])).StatusCode);
        Assert.Equal(current.Revision, (await h.Read()).Revision);
    }

    [LocalPostgresFact]
    public async Task MatchingSelectionNoOpPreservesRevisionAndStaleNoOpIsRejected()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        Assert.Equal(HttpStatusCode.NoContent, (await h.Select(before.Revision, [], [])).StatusCode); Assert.Equal(before.Revision, (await h.Read()).Revision);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Select(before.Revision, [h.Skill], [])).StatusCode); var current = await h.Read();
        Assert.Equal(HttpStatusCode.NoContent, (await h.Select(current.Revision, [h.Skill], [])).StatusCode); Assert.Equal(current.Revision, (await h.Read()).Revision);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Select(before.Revision, [h.Skill], [])).StatusCode);
    }

    [LocalPostgresFact]
    public async Task ConcurrentHttpUpdatesHaveExactlyOneCommittedRevision()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        var results = await Task.WhenAll(h.Select(before.Revision, [h.Skill], []), h.Select(before.Revision, [h.OtherSkill], []));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var after = await h.Read(); Assert.Single(after.TeachingSkills); Assert.Equal(Revision(before).Version + 1, Revision(after).Version);
    }

    [LocalPostgresFact]
    public async Task GoalHttpRevocationAtCommitRollsBackActualGoalAndRevision()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read(); bool reached = false;
        h.Checkpoint = (phase, _) => { if (phase == "before-commit") { reached = true; h.Revoked = true; } return Task.CompletedTask; };
        var response = await h.Send(HttpMethod.Post, "/goals", new { expectedRevision = before.Revision, text = "SYNTHETIC-REVOKED-GOAL" });
        Assert.True(reached); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); h.Checkpoint = null; h.Revoked = false;
        var after = await h.Read(); Assert.Empty(after.Goals); Assert.Equal(before.Revision, after.Revision);
        await using var db = LearningPostgresFixture.Open(h.Fixture); Assert.Empty(await db.Set<LearningMutationReceiptRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task RequestCancellationAfterPreparationRollsBackWithoutSuccessOrPartialState()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read(); using var cts = new CancellationTokenSource();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Checkpoint = async (phase, ct) => { if (phase == "prepared") { prepared.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var request = h.Select(before.Revision, [h.Skill], [], ct: cts.Token);
        try { await prepared.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { cts.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); h.Checkpoint = null;
        var after = await h.Read(); Assert.Equal(before.Revision, after.Revision); Assert.Empty(after.TeachingSkills);
    }

    [LocalPostgresFact]
    public async Task PostPreparationFailureRollsBackAndDoesNotLogPrivateExceptionPayloads()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        h.Checkpoint = (phase, _) => phase == "prepared" ? Task.FromException(new InvalidOperationException(ControlledLearningHttpPostgresHost.PrivateMarker)) : Task.CompletedTask;
        var response = await h.Select(before.Revision, [h.Skill], []); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(ControlledLearningHttpPostgresHost.PrivateMarker, await response.Content.ReadAsStringAsync()); h.Checkpoint = null;
        Assert.Equal(before.Revision, (await h.Read()).Revision); Assert.Empty((await h.Read()).TeachingSkills);
        Assert.NotEmpty(h.Logs); Assert.Contains(h.Logs, log => log.Contains("Request", StringComparison.Ordinal));
        Assert.All(h.Logs, log => Assert.DoesNotContain(ControlledLearningHttpPostgresHost.PrivateMarker, log));
    }

    [LocalPostgresFact]
    public async Task UnavailableAuthorityAndPrivateInfrastructureExceptionsReturnOnlySanitizedProblems()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read();
        h.Unavailable = true;
        var read = await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root); Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode); Assert.DoesNotContain(before.Revision, await read.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.Select(before.Revision, [h.Skill], [])).StatusCode);
        h.Unavailable = false; h.ThrowPrivate = true; var error = await h.Client.GetAsync(ControlledLearningSetupEndpoints.Root); Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.DoesNotContain(ControlledLearningHttpPostgresHost.PrivateMarker, await error.Content.ReadAsStringAsync());
        h.ThrowPrivate = false; Assert.Equal(before.Revision, (await h.Read()).Revision); Assert.All(h.Logs, log => Assert.DoesNotContain(ControlledLearningHttpPostgresHost.PrivateMarker, log));
    }

    [LocalPostgresFact]
    public async Task LostCommitAcknowledgementReturnsUnavailableButCommittedReceiptAndStateRemainReconcilable()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var before = await h.Read(); var operation = Guid.NewGuid();
        // Read preflight has its own transaction: inject only at the mutation commit checkpoint.
        h.Checkpoint = (phase, _) => { if (phase == "before-commit") h.LoseCommitAcknowledgement = true; return Task.CompletedTask; };
        var response = await h.Select(before.Revision, [h.Skill], [], operation); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        h.Checkpoint = null; var after = await h.Read(); Assert.Equal(1, Revision(after).Version); Assert.Equal(h.Skill, Assert.Single(after.TeachingSkills));
        await using var db = LearningPostgresFixture.Open(h.Fixture); var receipt = Assert.Single(await db.Set<LearningMutationReceiptRow>().ToListAsync());
        Assert.Equal(operation, receipt.OperationId); Assert.Equal(0, receipt.ExpectedVersion); Assert.Equal(1, receipt.ResultVersion);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Select(before.Revision, [h.Skill], [], operation)).StatusCode); Assert.Equal(after.Revision, (await h.Read()).Revision);
    }

    [LocalPostgresFact]
    public async Task ForeignProfilesAndOtherModulesStayIsolatedInDatabaseAndHttp()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var other = new Andril.Learning.Interfaces.LearningSetupScope(Guid.NewGuid(), Guid.NewGuid());
        await LearningPostgresFixture.Profile(h.Fixture, other); await LearningPostgresFixture.Profile(h.Fixture, new(Guid.NewGuid(), Guid.NewGuid()), module: "hobbies");
        await LearningPostgresFixture.Profile(h.Fixture, new(Guid.NewGuid(), Guid.NewGuid()), module: "social");
        await using (var db = LearningPostgresFixture.Open(h.Fixture)) await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_learning.learning_personal_goals(id,learning_profile_id,module,text) VALUES({Guid.NewGuid()},{other.LearningProfileId},'learning','SYNTHETIC-FOREIGN-GOAL')");
        var before = await h.Read(); Assert.Empty(before.Goals); Assert.Equal(HttpStatusCode.NoContent, (await h.Select(before.Revision, [h.Skill], [])).StatusCode);
        await using var owner = LearningPostgresFixture.Open(h.Fixture); var foreign = await owner.Set<LearningProfileRevisionRow>().SingleAsync(r => r.LearningProfileId == other.LearningProfileId);
        Assert.Equal(1, foreign.Version); Assert.Equal("SYNTHETIC-FOREIGN-GOAL", (await owner.PersonalGoals.SingleAsync()).Text);
        Assert.Equal(2, await owner.Set<LearningProfileRevisionRow>().CountAsync());
        await using var runtime = h.OpenRuntime(); await runtime.Database.OpenConnectionAsync();
        await runtime.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{h.Subject.ToString()},false),set_config('andril.account',{h.Account.ToString()},false),set_config('andril.module','learning',false)");
        Assert.Empty(await runtime.PersonalGoals.Where(g => g.LearningProfileId == other.LearningProfileId).ToListAsync());
    }

    [LocalPostgresFact]
    public async Task RuntimeCannotExpandScopeModifyRevisionOrAcquireInfrastructurePrivileges()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); await using var runtime = h.OpenRuntime(); await runtime.Database.OpenConnectionAsync();
        foreach (var sql in new[] { "SELECT * FROM andril_identity.accounts", "UPDATE andril_learning.learning_profile_revisions SET version=0", "UPDATE andril_learning.learning_personal_goals SET module='social'", "DELETE FROM andril_learning.learning_revision_generations", "CREATE TABLE andril_learning.forbidden(id integer)" })
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlRawAsync(sql))).SqlState);
        Assert.False(await runtime.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task ControlledOpenApiDescribesConditionalGoalAndSelectionOperationsWithoutPrivateValues()
    {
        await using var h = await ControlledLearningHttpPostgresHost.Create(); var response = await h.Client.GetAsync("/openapi/v1.json"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(text); var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty(ControlledLearningSetupEndpoints.Root).TryGetProperty("get", out _));
        var update = paths.GetProperty(ControlledLearningSetupEndpoints.Root + "/selections").GetProperty("put");
        Assert.Contains(update.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "Idempotency-Key" && p.GetProperty("required").GetBoolean());
        Assert.Contains("expectedRevision", text); Assert.Contains("supabaseBearer", text); Assert.DoesNotContain(h.Account.ToString(), text); Assert.DoesNotContain(h.Profile.ToString(), text);
    }
}
