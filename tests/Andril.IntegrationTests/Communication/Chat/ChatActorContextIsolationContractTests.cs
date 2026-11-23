using Fixture = Andril.IntegrationTests.Communication.Chat.ChatSyntheticAuthorizationFixture;
using System.Reflection;
using Andril.API.Extensions;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat;

public sealed class ChatActorContextIsolationContractTests
{
    private static readonly Guid Conversation = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherConversation = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid Account = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherAccount = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid Subject = Guid.Parse("30000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherSubject = Guid.Parse("30000000-0000-4000-8000-000000000002");

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task Existing_policy_resolves_exact_synthetic_server_identity_and_each_operation_reads_fresh_evidence(ChatModule module)
    {
        var fixture = new Fixture();
        var actor = await fixture.Policy.AuthorizeConversationAsync(module, Conversation);
        Assert.NotNull(actor);
        Assert.Equal(Subject, actor.VerifiedSubjectId);
        Assert.Equal(Account, actor.AccountId);
        Assert.Equal(Fixture.Profile(module, Account), actor.ModuleProfileId);
        Assert.Equal(module, actor.Module);
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        Assert.True(slot.BeginSimulatedTransaction(request, transaction));
        Assert.True(await slot.BindOperationAsync(request, transaction, module, Conversation));
        Assert.True(slot.HasSimulatedContext(request, transaction, module, Conversation));
        Assert.True(await slot.BindOperationAsync(request, transaction, module, Conversation));
        Assert.Equal(3, fixture.Reads);
        Assert.False(slot.MayUseDatabase);
        Assert.Null(await new DenyAllChatAccessAuthorizer().AuthorizeConversationAsync(module, Conversation));
    }

    [Theory]
    [InlineData("null-user")]
    [InlineData("unauthenticated")]
    [InlineData("missing-subject")]
    [InlineData("empty-subject")]
    [InlineData("missing-reader")]
    [InlineData("missing-authorizer")]
    public async Task Missing_server_identity_or_authoritative_boundary_fails_closed(string scenario)
    {
        var fixture = new Fixture(scenario == "missing-reader", scenario == "null-user");
        if (scenario == "unauthenticated") fixture.User.IsAuthenticated = false;
        if (scenario == "missing-subject") fixture.User.UserId = null;
        if (scenario == "empty-subject") fixture.User.UserId = Guid.Empty;
        var slot = new ChatActorContextIsolationContract(scenario == "missing-authorizer" ? null : fixture.Policy,
            scenario == "null-user" ? null : fixture.User);
        var request = new object();
        var transaction = new object();
        slot.BeginSimulatedTransaction(request, transaction);
        Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
        Assert.Equal(0, fixture.Reads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public async Task Unknown_modules_are_denied_by_the_real_policy_and_simulated_context(int value)
    {
        var fixture = new Fixture();
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        slot.BeginSimulatedTransaction(request, transaction);
        Assert.Null(await fixture.Policy.AuthorizeConversationAsync((ChatModule)value, Conversation));
        Assert.False(await slot.BindOperationAsync(request, transaction, (ChatModule)value, Conversation));
        Assert.Equal(0, fixture.Reads);
    }

    [Theory]
    [InlineData("missing-evidence")]
    [InlineData("forged-profile")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-subject")]
    [InlineData("wrong-module")]
    [InlineData("wrong-conversation")]
    [InlineData("request-conflict")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("expired")]
    public async Task Forged_conflicting_or_stale_evidence_is_rejected_by_existing_policy(string scenario)
    {
        var fixture = new Fixture();
        fixture.Transform = evidence =>
        {
            if (scenario == "missing-evidence") return null;
            switch (scenario)
            {
                case "forged-profile": Set(evidence, "ModuleProfileId", Fixture.Profile(ChatModule.Learning, OtherAccount)); break;
                case "wrong-owner": Set(evidence, "ProfileAccountId", OtherAccount); break;
                case "wrong-subject": Set(evidence, "AccountSubjectId", OtherSubject); break;
                case "wrong-module": Set(evidence, "ProfileModule", ChatModule.Hobbies); break;
                case "wrong-conversation": Set(evidence, "Conversation", fixture.ConversationFor(ChatModule.Social, OtherConversation)); break;
                case "request-conflict": Set(evidence, "RequestId", Guid.Empty); break;
                case "stale": Set(evidence, "ObservedAtUtc", Fixture.Now.AddTicks(-1)); break;
                case "future": Set(evidence, "ObservedAtUtc", Fixture.Now.AddTicks(1)); break;
                case "expired": Set(evidence, "ValidUntilUtc", Fixture.Now); break;
            }
            return evidence;
        };
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        slot.BeginSimulatedTransaction(request, transaction);
        Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        Assert.Equal(1, fixture.Reads);
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
    }

    public static IEnumerable<object[]> MissingEligibility()
    {
        foreach (var flag in new[] { "IsCurrent", "IsProfileActive", "IsProfileEligible", "IsParticipationAuthorized",
            "IsModuleCommunicationEligible", "IsAdult", "HasSocialOptIn", "HasMutualMatch" })
        {
            yield return [flag, null!];
            yield return [flag, false];
        }
        foreach (var flag in new[] { "IsBlocked", "IsRevoked" })
        {
            yield return [flag, null!];
            yield return [flag, true];
        }
    }

    [Theory]
    [MemberData(nameof(MissingEligibility))]
    public async Task Missing_inactive_ineligible_blocked_or_unmatched_evidence_cannot_bind_context(string flag, bool? value)
    {
        var fixture = new Fixture { Transform = evidence => { Set(evidence, flag, value); return evidence; } };
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        slot.BeginSimulatedTransaction(request, transaction);
        Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        Assert.Equal(1, fixture.Reads);
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
    }

    [Fact]
    public async Task Previously_bound_context_cannot_replace_rechecking_changed_eligibility()
    {
        var fixture = new Fixture();
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        slot.BeginSimulatedTransaction(request, transaction);
        Assert.True(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        fixture.Transform = evidence => { Set(evidence, "IsBlocked", true); return evidence; };
        Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        Assert.Equal(2, fixture.Reads);
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
    }

    [Fact]
    public async Task Reusing_a_simulated_slot_for_another_account_and_module_requires_new_evidence()
    {
        var fixture = new Fixture();
        var slot = fixture.Slot();
        var requestA = new object();
        var transactionA = new object();
        slot.BeginSimulatedTransaction(requestA, transactionA);
        Assert.True(await slot.BindOperationAsync(requestA, transactionA, ChatModule.Learning, Conversation));
        Assert.False(slot.HasSimulatedContext(requestA, transactionA, ChatModule.Hobbies, Conversation));
        fixture.User.UserId = OtherSubject;
        fixture.AccountId = OtherAccount;
        Assert.False(slot.HasSimulatedContext(requestA, transactionA, ChatModule.Learning, Conversation));
        var requestB = new object();
        var transactionB = new object();
        slot.BeginSimulatedTransaction(requestB, transactionB);
        Assert.False(slot.HasSimulatedContext(requestB, transactionB, ChatModule.Hobbies, Conversation));
        Assert.True(await slot.BindOperationAsync(requestB, transactionB, ChatModule.Hobbies, Conversation));
        Assert.False(slot.HasSimulatedContext(requestA, transactionA, ChatModule.Learning, Conversation));
        Assert.Equal(2, fixture.Reads);
        Assert.NotEqual(Fixture.Profile(ChatModule.Learning, Account), Fixture.Profile(ChatModule.Hobbies, OtherAccount));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task Ending_a_simulated_transaction_never_preserves_context_for_a_later_operation(string outcome)
    {
        var fixture = new Fixture();
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        slot.BeginSimulatedTransaction(request, transaction);
        Assert.True(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        if (outcome == "failed")
        {
            fixture.Transform = _ => throw new InvalidOperationException("synthetic_transaction_failure");
            Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        }
        if (outcome == "cancelled")
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slot.BindOperationAsync(request, transaction,
                ChatModule.Social, Conversation, cancellation.Token));
        }
        slot.EndSimulatedTransaction();
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
        Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        slot.BeginSimulatedTransaction(request, new object());
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
        Assert.Equal(outcome == "failed" ? 2 : 1, fixture.Reads);
    }

    [Fact]
    public async Task Missing_transaction_or_unknown_request_cannot_reuse_or_create_context()
    {
        var fixture = new Fixture();
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        Assert.False(slot.BeginSimulatedTransaction(request, null));
        Assert.False(slot.BeginSimulatedTransaction(null, transaction));
        slot.BeginSimulatedTransaction(request, transaction);
        Assert.False(await slot.BindOperationAsync(new object(), transaction, ChatModule.Social, Conversation));
        Assert.False(await slot.BindOperationAsync(request, new object(), ChatModule.Social, Conversation));
        Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Guid.Empty));
        Assert.Equal(0, fixture.Reads);
    }

    [Fact]
    public async Task Cancellation_before_or_after_binding_cannot_leave_observable_context()
    {
        var fixture = new Fixture();
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        slot.BeginSimulatedTransaction(request, transaction);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slot.BindOperationAsync(request, transaction,
            ChatModule.Social, Conversation, cancelled.Token));
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
        using var later = new CancellationTokenSource();
        slot.BeginSimulatedTransaction(request, transaction);
        Assert.True(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation, later.Token));
        later.Cancel();
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
        Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        Assert.Equal(1, fixture.Reads);
    }

    [Fact]
    public async Task Cancellation_while_evidence_is_pending_does_not_promote_the_late_result()
    {
        var fixture = new Fixture();
        var held = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.HoldNext = held;
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        slot.BeginSimulatedTransaction(request, transaction);
        using var cancellation = new CancellationTokenSource();
        var pending = slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation, cancellation.Token);
        var evidence = fixture.LastEvidence;
        Assert.NotNull(evidence);
        cancellation.Cancel();
        held.SetResult(evidence);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_evidence_cannot_overwrite_a_new_request_or_new_operation_in_the_simulated_slot(bool sameTransaction)
    {
        var fixture = new Fixture();
        var held = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.HoldNext = held;
        var slot = fixture.Slot();
        var requestA = new object();
        var transactionA = new object();
        slot.BeginSimulatedTransaction(requestA, transactionA);
        var old = slot.BindOperationAsync(requestA, transactionA, ChatModule.Learning, Conversation);
        var oldEvidence = fixture.LastEvidence;
        Assert.NotNull(oldEvidence);
        var requestB = sameTransaction ? requestA : new object();
        var transactionB = sameTransaction ? transactionA : new object();
        if (!sameTransaction) slot.BeginSimulatedTransaction(requestB, transactionB);
        Assert.True(await slot.BindOperationAsync(requestB, transactionB, ChatModule.Hobbies, OtherConversation));
        held.SetResult(oldEvidence);
        Assert.False(await old);
        Assert.True(slot.HasSimulatedContext(requestB, transactionB, ChatModule.Hobbies, OtherConversation));
        Assert.False(slot.HasSimulatedContext(requestA, transactionA, ChatModule.Learning, Conversation));
    }

    [Fact]
    public async Task Dependency_failure_and_forged_client_actor_do_not_supply_context_or_private_diagnostics()
    {
        var fixture = new Fixture();
        var slot = fixture.Slot();
        var request = new object();
        var transaction = new object();
        slot.BeginSimulatedTransaction(request, transaction);
        Assert.True(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        fixture.Transform = _ => throw new InvalidOperationException("synthetic_private_dependency_details");
        Assert.False(await slot.BindOperationAsync(request, transaction, ChatModule.Social, Conversation));
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, Conversation));
        var forged = new ChatAuthorizedActor(Subject, OtherAccount, Fixture.Profile(ChatModule.Social, OtherAccount), ChatModule.Social, Conversation);
        var denied = new ChatActorContextIsolationContract(new DenyAllChatAccessAuthorizer(), fixture.User);
        denied.BeginSimulatedTransaction(request, transaction);
        Assert.False(await denied.BindOperationAsync(request, transaction, forged.Module, forged.ConversationId));
        Assert.DoesNotContain(typeof(ChatActorContextIsolationContract).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SelectMany(method => method.GetParameters()), parameter => parameter.ParameterType == typeof(ChatAuthorizedActor));
        Assert.Equal(nameof(ChatAuthorizedActor), forged.ToString());
        Assert.Equal(nameof(ChatActorContextIsolationContract), slot.ToString());
        Assert.Equal("ChatConversationAccessEvidence", fixture.LastEvidence!.ToString());
        Assert.DoesNotContain(OtherAccount.ToString(), $"{forged} {slot} {fixture.LastEvidence}", StringComparison.Ordinal);
    }

    [Fact]
    public void Privileged_runtime_roles_and_production_activation_remain_rejected_by_existing_contracts()
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        foreach (var privilege in new[] { ChatDeclaredPrivilege.Superuser, ChatDeclaredPrivilege.BypassRls, ChatDeclaredPrivilege.OwnChatTables })
            Assert.False(ChatPostgresSecurityContract.Validate(design with { Runtime = design.Runtime! with { Inherited = privilege } }).ClosedBaselineValid);
        var services = new ServiceCollection();
        services.AddAccounts(new ConfigurationBuilder().Build());
        services.AddNotifications();
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChatAccessAuthorizer)
            || descriptor.ServiceType.Assembly == typeof(ChatActorContextIsolationContract).Assembly);
        Assert.True(typeof(ChatActorContextIsolationContract).IsNotPublic);
        Assert.Equal(typeof(ChatActorContextIsolationContractTests).Assembly, typeof(ChatActorContextIsolationContract).Assembly);
    }

    private static void Set(object evidence, string property, object? value) => Fixture.Set(evidence, property, value);
}
