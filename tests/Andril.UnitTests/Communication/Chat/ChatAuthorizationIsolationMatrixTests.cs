using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatAuthorizationIsolationMatrixTests
{
    private static readonly Guid Subject = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Account = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Profile = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Conversation = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid Foreign = Guid.Parse("90000000-0000-0000-0000-000000000009");
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> ConversationTargets()
    {
        foreach (var module in Enum.GetValues<ChatModule>())
        foreach (var operation in Enum.GetValues<ChatOperation>().Where(value => value != ChatOperation.ListConversations))
            yield return [module, operation];
    }

    public static IEnumerable<object[]> BindingCases()
    {
        string[] faults =
        [
            "subject", "account", "profile-owner", "private-profile", "module", "conversation",
            "evaluation", "binding-request", "participants", "currentness", "expired", "missing-binding"
        ];
        foreach (var target in ConversationTargets())
        foreach (var fault in faults)
            yield return [target[0], target[1], fault];
    }

    public static IEnumerable<object[]> OutcomeCases()
    {
        foreach (var target in ConversationTargets())
        foreach (var outcome in new[] { "denied", "blocked", "revoked", "positive" })
            yield return [target[0], target[1], outcome];
    }

    public static IEnumerable<object[]> OperationPairs()
    {
        foreach (var module in Enum.GetValues<ChatModule>())
        foreach (var requested in Enum.GetValues<ChatOperation>())
        foreach (var supplied in Enum.GetValues<ChatOperation>())
            if (requested != supplied)
                yield return [module, requested, supplied];
    }

    public static IEnumerable<object[]> AuthorityCases()
    {
        foreach (var target in ConversationTargets())
        foreach (var missing in new[] { "m2-context", "m5-reader", "reader-result" })
            yield return [target[0], target[1], missing];
    }

    public static IEnumerable<object[]> UnknownOperations()
    {
        foreach (var module in Enum.GetValues<ChatModule>())
        foreach (var value in new[] { 0, -1, 5, int.MaxValue })
            yield return [module, value];
    }

    public static IEnumerable<object[]> DependencyCases()
    {
        foreach (var target in ConversationTargets())
        foreach (var failure in new[] { "exception", "dependency-cancellation" })
            yield return [target[0], target[1], failure];
    }

    public static IEnumerable<object[]> AllTargets()
    {
        foreach (var module in Enum.GetValues<ChatModule>())
        foreach (var operation in Enum.GetValues<ChatOperation>())
            yield return [module, operation];
    }

    // Finite, fresh Denied controls ensure binding checks are exercised rather than
    // hidden by the sentinel expiry used in some original D3 fixture scenarios.
    [Theory]
    [MemberData(nameof(BindingCases))]
    public async Task ForeignOrIncompleteBindingCannotEstablishAuthoritativeDenial(
        ChatModule module, ChatOperation operation, string fault)
    {
        var reader = new Reader
        {
            Transform = evidence => fault switch
            {
                "evaluation" => evidence with { EvaluationId = Foreign },
                "missing-binding" => evidence with { Binding = null },
                _ => evidence with { Binding = Corrupt(evidence.Binding!, module, fault) }
            }
        };

        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await Policy(reader).AuthorizeAsync(Target(module, operation)));
        Assert.Equal(1, reader.Calls);
    }

    [Theory]
    [MemberData(nameof(OutcomeCases))]
    public async Task BoundFiniteOutcomeStaysOperationAndModuleScoped(
        ChatModule module, ChatOperation operation, string outcome)
    {
        var reader = new Reader
        {
            Transform = evidence => evidence with
            {
                Outcome = outcome == "denied" ? ChatAuthorizationOutcome.Denied : ChatAuthorizationOutcome.Allowed,
                Binding = evidence.Binding! with
                {
                    IsBlocked = outcome == "blocked",
                    IsRevoked = outcome == "revoked"
                }
            }
        };
        var expected = outcome == "positive" ? ChatAuthorizationOutcome.Unavailable : ChatAuthorizationOutcome.Denied;

        Assert.Equal(expected, await Policy(reader).AuthorizeAsync(Target(module, operation)));
        Assert.Equal(1, reader.Calls);
    }

    [Theory]
    [MemberData(nameof(OperationPairs))]
    public async Task EvidenceForAnotherOperationCannotAuthorizeOrProhibitTheRequestedOperation(
        ChatModule module, ChatOperation requested, ChatOperation supplied)
    {
        var reader = new Reader
        {
            Transform = evidence => evidence with { Request = Target(module, supplied) }
        };

        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await Policy(reader).AuthorizeAsync(Target(module, requested)));
        Assert.Equal(requested == ChatOperation.ListConversations ? 0 : 1, reader.Calls);
    }

    [Theory]
    [MemberData(nameof(AuthorityCases))]
    public async Task MissingOwnerAuthorityNeverFallsBackToLegacy(
        ChatModule module, ChatOperation operation, string missing)
    {
        var reader = new Reader { ReturnMissing = missing == "reader-result" };
        var policy = new ChatConversationAccessPolicy(
            missing == "m2-context" ? null : new User(),
            missing == "m5-reader" ? null : reader, new Clock());

        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await policy.AuthorizeAsync(Target(module, operation)));
        Assert.Equal(missing == "reader-result" ? 1 : 0, reader.Calls);
    }

    [Theory]
    [MemberData(nameof(ConversationTargets))]
    public async Task PreviousEvaluationCannotBeReusedEvenForTheSameTarget(ChatModule module, ChatOperation operation)
    {
        ChatOperationAccessEvidence? cached = null;
        var reader = new Reader
        {
            Transform = evidence => cached ??= evidence
        };
        var policy = Policy(reader);
        var target = Target(module, operation);

        Assert.Equal(ChatAuthorizationOutcome.Denied, await policy.AuthorizeAsync(target));
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await policy.AuthorizeAsync(target));
        Assert.Equal(2, reader.Evaluations.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(ConversationTargets))]
    public async Task TrustedSubjectChangeDuringReadInvalidatesEvidence(ChatModule module, ChatOperation operation)
    {
        var user = new User();
        var reader = new Reader { DuringRead = () => user.UserId = Foreign };

        Assert.Equal(ChatAuthorizationOutcome.Unavailable,
            await new ChatConversationAccessPolicy(user, reader, new Clock()).AuthorizeAsync(Target(module, operation)));
    }

    [Fact]
    public async Task OverlappingRequestsCannotExchangeTheirEvaluationEvidence()
    {
        var pending = new List<TaskCompletionSource<ChatOperationAccessEvidence?>>();
        var evidenceByRequest = new List<ChatOperationAccessEvidence>();
        var reader = new Reader
        {
            PendingRead = evidence =>
            {
                evidenceByRequest.Add(evidence);
                var completion = new TaskCompletionSource<ChatOperationAccessEvidence?>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(completion);
                return completion.Task;
            }
        };
        var policy = Policy(reader);
        var first = policy.AuthorizeAsync(Target(ChatModule.Learning, ChatOperation.ReadMessageHistory));
        var second = policy.AuthorizeAsync(Target(ChatModule.Hobbies, ChatOperation.SubmitMessage));
        Assert.Equal(2, pending.Count);
        Assert.NotEqual(evidenceByRequest[0].EvaluationId, evidenceByRequest[1].EvaluationId);

        pending[1].SetResult(evidenceByRequest[0]);
        pending[0].SetResult(evidenceByRequest[1]);

        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await first);
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await second);
    }

    [Theory]
    [MemberData(nameof(UnknownOperations))]
    public async Task UnknownOperationIsDeniedBeforeReadingAnyAuthority(ChatModule module, int operation)
    {
        var reader = new Reader();
        Assert.Equal(ChatAuthorizationOutcome.Denied,
            await Policy(reader).AuthorizeAsync(new((ChatOperation)operation, module, Conversation)));
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [MemberData(nameof(ConversationTargets))]
    public async Task CallerCancellationWinsOverOtherwiseValidEvidence(ChatModule module, ChatOperation operation)
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new Reader { DuringRead = () => cancellation.Cancel() };
        var policy = Policy(reader);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(
            () => policy.AuthorizeAsync(Target(module, operation), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(cancellation.Token, reader.LastToken);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => policy.AuthorizeAsync(Target(module, operation), cancellation.Token));
        Assert.Equal(1, reader.Calls);
    }

    [Theory]
    [MemberData(nameof(DependencyCases))]
    public async Task DependencyFailureReturnsOnlyAnOpaqueUnavailableOutcome(
        ChatModule module, ChatOperation operation, string failure)
    {
        var reader = new Reader
        {
            Failure = failure == "exception" ? new InvalidOperationException(Profile.ToString()) : new OperationCanceledException(Account.ToString())
        };
        var result = await Policy(reader).AuthorizeAsync(Target(module, operation));

        Assert.Equal(ChatAuthorizationOutcome.Unavailable, result);
        Assert.Equal("Unavailable", result.ToString());
        Assert.DoesNotContain(Profile.ToString(), result.ToString());
        Assert.DoesNotContain(Account.ToString(), result.ToString());
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public async Task ProductionFallbackRemainsNonGrantingForEveryModuleAndOperation(ChatModule module, ChatOperation operation)
    {
        IChatAccessAuthorizer fallback = new DenyAllChatAccessAuthorizer();
        Assert.Equal(ChatAuthorizationOutcome.Unavailable, await fallback.AuthorizeAsync(Target(module, operation)));
        Assert.Null(await fallback.AuthorizeConversationAsync(module, Conversation));
    }

    private static ChatAuthorizationRequest Target(ChatModule module, ChatOperation operation) =>
        new(operation, module, operation == ChatOperation.ListConversations ? null : Conversation);

    private static ChatConversationAccessPolicy Policy(Reader reader) => new(new User(), reader, new Clock());

    private static ChatConversationAccessEvidence Corrupt(ChatConversationAccessEvidence binding, ChatModule module, string fault) =>
        fault switch
        {
            "subject" => binding with { AccountSubjectId = Foreign },
            "account" => binding with { AccountId = Foreign, ProfileAccountId = Foreign },
            "profile-owner" => binding with { ProfileAccountId = Foreign },
            "private-profile" => binding with { ModuleProfileId = Foreign },
            "module" => binding with { ProfileModule = module == ChatModule.Learning ? ChatModule.Hobbies : ChatModule.Learning },
            "conversation" => binding with { Conversation = ChatConversation.Create(module, Foreign, []) },
            "binding-request" => binding with { RequestId = Foreign },
            "participants" => binding with { Conversation = ChatConversation.Create(module, Conversation, []) },
            "currentness" => binding with { IsCurrent = false },
            "expired" => binding with { ValidUntilUtc = Now },
            _ => throw new InvalidOperationException("Unknown test mutation.")
        };

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class User : ICurrentUser
    {
        public Guid? UserId { get; set; } = Subject;
        public bool IsAuthenticated => true;
    }

    private sealed class Reader : IChatConversationAccessEvidenceReader
    {
        internal Func<ChatOperationAccessEvidence, ChatOperationAccessEvidence> Transform { get; init; } = evidence => evidence;
        internal Func<ChatOperationAccessEvidence, Task<ChatOperationAccessEvidence?>>? PendingRead { get; init; }
        internal Action? DuringRead { get; init; }
        internal Exception? Failure { get; init; }
        internal bool ReturnMissing { get; init; }
        internal int Calls { get; private set; }
        internal CancellationToken LastToken { get; private set; }
        internal List<Guid> Evaluations { get; } = [];

        public Task<ChatConversationAccessEvidence?> ReadAsync(
            Guid subject, ChatModule module, Guid conversation, Guid evaluation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The operation evaluator must never call the legacy reader.");

        public Task<ChatOperationAccessEvidence?> ReadOperationAsync(
            Guid subject, ChatAuthorizationRequest request, Guid evaluation, CancellationToken cancellationToken)
        {
            Calls++;
            Evaluations.Add(evaluation);
            LastToken = cancellationToken;
            DuringRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            if (ReturnMissing) return Task.FromResult<ChatOperationAccessEvidence?>(null);
            var binding = new ChatConversationAccessEvidence
            {
                RequestId = evaluation, AccountSubjectId = subject, AccountId = Account, ProfileAccountId = Account,
                ModuleProfileId = Profile, ProfileModule = request.Module,
                Conversation = ChatConversation.Create(request.Module, Conversation,
                    [ChatParticipantReference.Create(request.Module, Account, Profile)]),
                // Fixed finite test evidence. This defines no production evidence lifetime.
                ObservedAtUtc = Now, ValidUntilUtc = Now.AddTicks(1), IsCurrent = true,
                IsProfileActive = true, IsProfileEligible = true, IsParticipationAuthorized = true,
                IsModuleCommunicationEligible = true, IsBlocked = false, IsRevoked = false,
                IsAdult = true, HasSocialOptIn = true, HasMutualMatch = true
            };
            var evidence = new ChatOperationAccessEvidence(evaluation, request, ChatAuthorizationOutcome.Denied, binding);
            Assert.Equal(nameof(ChatOperationAccessEvidence), evidence.ToString());
            Assert.Equal(nameof(ChatConversationAccessEvidence), binding.ToString());
            Assert.Equal(nameof(ChatAuthorizationRequest), request.ToString());
            return PendingRead is null
                ? Task.FromResult<ChatOperationAccessEvidence?>(Transform(evidence))
                : PendingRead(evidence);
        }
    }
}
