using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Safety;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Synthetic transport/coordination tests; no real classifier, reviewer or transaction.</summary>
public sealed class AiSafetyReviewServiceTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private static AiAuthorizedActor Actor(AiModule module = AiModule.Learning) => new(Id(1), Id(10 + (int)module), module);
    private static AiSafetySubject Subject(AiModule module = AiModule.Learning, AiSafetyStage stage = AiSafetyStage.UserInput) =>
        new(new(new(Actor(module), Id(20), Id(21)), Id(22)), Id(23), Id(24), stage, "synthetic-request-hash");
    private static AiReviewRequestBinding Report() => new(Actor(), Id(20), Id(23), Id(25), AiReviewOrigin.UserReport, Id(25));
    private static AiTextResponseInput Output(string text = "Synthetic output") => new(new(text, false), AiTextCompletionState.Completed,
        AiTextOutputKind.Text, "synthetic-model", "synthetic-request-hash", new(100, 100, 0, 100, true));
    private static AiTextResponseExpectation Expected() => new("synthetic-model", "synthetic-request-hash", false);
    private static AiValidatedSafetyFlag Flag(AiSafetySubject subject) => new(subject, Id(30), Id(31), true, true, true, true, true);

    [Theory]
    [InlineData("authorization")]
    [InlineData("privacy")]
    [InlineData("gateway")]
    public void A_NullDependency_IsRejected(string name)
    {
        var s = new Setup();
        var error = Assert.Throws<ArgumentNullException>(() => new AiSafetyReviewService(name == "authorization" ? null! : s.Auth,
            name == "privacy" ? null! : s.Privacy, name == "gateway" ? null! : s.Gateway));
        Assert.Equal(name, error.ParamName);
    }

    [Theory]
    [InlineData("module")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("operation")]
    [InlineData("conversation")]
    [InlineData("attempt")]
    [InlineData("message")]
    [InlineData("revision")]
    [InlineData("stage")]
    [InlineData("binding")]
    public async Task A_InvalidSubject_DoesNotReachDependencies(string defect)
    {
        var s = new Setup();
        var subject = Subject();
        subject = defect switch
        {
            "module" => subject with { Attempt = subject.Attempt with { Operation = subject.Attempt.Operation with { Actor = Actor() with { Module = (AiModule)99 } } } },
            "account" => subject with { Attempt = subject.Attempt with { Operation = subject.Attempt.Operation with { Actor = Actor() with { UserId = Guid.Empty } } } },
            "profile" => subject with { Attempt = subject.Attempt with { Operation = subject.Attempt.Operation with { Actor = Actor() with { ModuleProfileId = Guid.Empty } } } },
            "operation" => subject with { Attempt = subject.Attempt with { Operation = subject.Attempt.Operation with { OperationId = Guid.Empty } } },
            "conversation" => subject with { Attempt = subject.Attempt with { Operation = subject.Attempt.Operation with { ConversationId = Guid.Empty } } },
            "attempt" => subject with { Attempt = subject.Attempt with { AttemptId = Guid.Empty } },
            "message" => subject with { MessageId = Guid.Empty },
            "revision" => subject with { ContentRevision = Guid.Empty },
            "stage" => subject with { Stage = (AiSafetyStage)99 },
            _ => subject with { RequestFingerprint = "" }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => s.Service.AssessInputAsync(subject, "Hello"));
        Assert.Empty(s.Auth.Calls);
        Assert.Empty(s.Gateway.Assessments);
    }

    [Theory]
    [InlineData(AiModule.Learning, "English synthetic text")]
    [InlineData(AiModule.Hobbies, "வணக்கம் தமிழ்")]
    [InlineData(AiModule.Social, "ආයුබෝවන්")]
    [InlineData(AiModule.Learning, "Vanakkam, explain pannunga")]
    public async Task B_AuthorizedModules_PreserveTextAndArguments(AiModule module, string text)
    {
        var s = new Setup();
        s.Auth.Actor = Actor(module);
        using var cts = new CancellationTokenSource();
        var result = await s.Service.AssessInputAsync(Subject(module), text, cts.Token);
        Assert.Equal(AiSafetyOutcome.ClearedForFurtherChecks, result.Outcome);
        var call = Assert.Single(s.Gateway.Assessments);
        Assert.Equal(Subject(module), call.Request.Subject);
        Assert.Equal(text, call.Request.Text);
        Assert.Equal(cts.Token, call.Token);
        Assert.Equal((module, cts.Token), Assert.Single(s.Auth.Calls));
        Assert.Equal((Actor(module), cts.Token), Assert.Single(s.Gate.Calls));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty-account")]
    [InlineData("empty-profile")]
    [InlineData("module")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("privacy")]
    public async Task B_UnauthorizedActorOrPrivacy_BlocksAssessment(string defect)
    {
        var s = new Setup();
        s.Auth.Actor = defect switch
        {
            "missing" => null,
            "empty-account" => Actor() with { UserId = Guid.Empty },
            "empty-profile" => Actor() with { ModuleProfileId = Guid.Empty },
            "module" => Actor(AiModule.Social),
            "account" => Actor() with { UserId = Id(99) },
            "profile" => Actor() with { ModuleProfileId = Id(99) },
            _ => Actor()
        };
        if (defect == "privacy") s.Gate.Outcome = AiPrivacyNoticeGateOutcome.NoticeRequired;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.AssessInputAsync(Subject(), "Hello"));
        Assert.Empty(s.Gateway.Assessments);
    }

    [Theory]
    [InlineData(AiSafetyOutcome.ClearedForFurtherChecks)]
    [InlineData(AiSafetyOutcome.WithholdPendingAssessment)]
    [InlineData(AiSafetyOutcome.SafetyResponseRequired)]
    [InlineData(AiSafetyOutcome.PolicyUnavailable)]
    public async Task C_ExplicitAssessmentOutcome_IsReturnedWithoutRouting(AiSafetyOutcome outcome)
    {
        var s = new Setup();
        s.Gateway.Assessment = request => new(request.Subject, request.ContentDigest, Id(40), outcome);
        Assert.Equal(outcome, (await s.Service.AssessInputAsync(Subject(), "Hello")).Outcome);
        Assert.Empty(s.Gateway.Routes);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("undefined")]
    [InlineData("null")]
    [InlineData("assessment")]
    [InlineData("digest")]
    [InlineData("operation")]
    [InlineData("attempt")]
    [InlineData("message")]
    [InlineData("stage")]
    [InlineData("revision")]
    public async Task C_MalformedOrStaleAssessment_IsRejected(string defect)
    {
        var s = new Setup();
        s.Gateway.Assessment = request =>
        {
            var subject = request.Subject;
            subject = defect switch
            {
                "operation" => subject with { Attempt = subject.Attempt with { Operation = subject.Attempt.Operation with { OperationId = Id(99) } } },
                "attempt" => subject with { Attempt = subject.Attempt with { AttemptId = Id(99) } },
                "message" => subject with { MessageId = Id(99) },
                "stage" => subject with { Stage = AiSafetyStage.AssistantOutput },
                "revision" => subject with { ContentRevision = Id(99) },
                _ => subject
            };
            return defect == "null" ? null! : new(subject, defect == "digest" ? "stale" : request.ContentDigest,
                defect == "assessment" ? Guid.Empty : Id(40), defect == "unknown" ? AiSafetyOutcome.Unknown :
                defect == "undefined" ? (AiSafetyOutcome)99 : AiSafetyOutcome.ClearedForFurtherChecks);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.AssessInputAsync(Subject(), "Hello"));
        Assert.Single(s.Gateway.Assessments);
    }

    [Fact]
    public async Task C_OutputAssessment_RevalidatesStructuralPolicyAndExactRequest()
    {
        var s = new Setup();
        var subject = Subject(stage: AiSafetyStage.AssistantOutput);
        var text = new string('x', 600); // Assistant output is token-limited, not 500 characters.
        Assert.Equal(AiSafetyOutcome.ClearedForFurtherChecks, (await s.Service.AssessOutputAsync(subject, Output(text), Expected())).Outcome);
        Assert.Equal(text, Assert.Single(s.Gateway.Assessments).Request.Text);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Service.AssessOutputAsync(subject, Output() with { Completion = AiTextCompletionState.Refused }, Expected()));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Service.AssessOutputAsync(subject with { RequestFingerprint = "foreign" }, Output(), Expected()));
        Assert.Single(s.Gateway.Assessments);
    }

    [Theory]
    [InlineData(AiReviewAcknowledgementOutcome.Accepted)]
    [InlineData(AiReviewAcknowledgementOutcome.Queued)]
    [InlineData(AiReviewAcknowledgementOutcome.Denied)]
    [InlineData(AiReviewAcknowledgementOutcome.PolicyUnavailable)]
    [InlineData(AiReviewAcknowledgementOutcome.Uncertain)]
    public async Task D_ReportAcknowledgement_IsAuthoritativeAndIndependentOfGenerationPrivacy(AiReviewAcknowledgementOutcome outcome)
    {
        var s = new Setup();
        s.Gate.Outcome = AiPrivacyNoticeGateOutcome.PolicyNotVerified;
        s.Gateway.Acknowledge = request => Ack(request, outcome);
        using var cts = new CancellationTokenSource();
        var result = await s.Service.SubmitUserReportAsync(Report(), cts.Token);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal((Report(), cts.Token), Assert.Single(s.Gateway.Reports));
        Assert.Empty(s.Gate.Calls);
        Assert.Single(s.Auth.Calls);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("origin")]
    [InlineData("trigger")]
    [InlineData("subject")]
    public async Task D_InvalidReportReference_IsRejected(string defect)
    {
        var s = new Setup();
        var report = defect switch
        {
            "empty" => Report() with { MessageId = Guid.Empty },
            "origin" => Report() with { Origin = (AiReviewOrigin)99 },
            "trigger" => Report() with { TriggerId = Id(99) },
            _ => Report() with { Subject = Subject() }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => s.Service.SubmitUserReportAsync(report));
        Assert.Empty(s.Gateway.Reports);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("undefined")]
    [InlineData("receipt")]
    [InlineData("request")]
    [InlineData("module")]
    [InlineData("conversation")]
    [InlineData("denial-receipt")]
    public async Task D_UnverifiedAcknowledgement_NeverClaimsAcceptance(string defect)
    {
        var s = new Setup();
        s.Gateway.Acknowledge = request => defect switch
        {
            "missing" => null!,
            "unknown" => new(request, AiReviewAcknowledgementOutcome.Unknown),
            "undefined" => new(request, (AiReviewAcknowledgementOutcome)99),
            "receipt" => new(request, AiReviewAcknowledgementOutcome.Accepted),
            "request" => Ack(request with { RequestId = Id(99) }),
            "module" => Ack(request with { Actor = Actor(AiModule.Social) }),
            "conversation" => Ack(request with { ConversationId = Id(99) }),
            _ => new(request, AiReviewAcknowledgementOutcome.Denied, Id(41))
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.SubmitUserReportAsync(Report()));
        Assert.Single(s.Gateway.Reports);
    }

    [Fact]
    public async Task D_ForeignReporter_IsDeniedBeforeGateway()
    {
        var s = new Setup();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.SubmitUserReportAsync(Report() with { Actor = Actor(AiModule.Social) }));
        Assert.Empty(s.Gateway.Reports);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unvalidated")]
    [InlineData("policy")]
    [InlineData("purpose")]
    [InlineData("permission")]
    [InlineData("deleted")]
    public async Task E_IncompleteFlagEvidence_DoesNotRoute(string defect)
    {
        var s = new Setup();
        s.Gateway.Flag = subject => defect switch
        {
            "missing" => null,
            "unvalidated" => Flag(subject) with { Validated = false },
            "policy" => Flag(subject) with { SeriousRoutingPolicyVerified = false },
            "purpose" => Flag(subject) with { PurposeApproved = false },
            "permission" => Flag(subject) with { EvidenceHandlingPermissionVerified = false },
            _ => Flag(subject) with { LifecyclePermits = false }
        };
        Assert.Equal(AiReviewAcknowledgementOutcome.PolicyUnavailable,
            (await s.Service.RouteValidatedFlagAsync(Subject(), Id(30), Id(25))).Outcome);
        Assert.Empty(s.Gateway.Routes);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module")]
    [InlineData("conversation")]
    [InlineData("attempt")]
    [InlineData("message")]
    [InlineData("revision")]
    [InlineData("flag")]
    public async Task E_ForeignFlagEvidence_IsRejected(string defect)
    {
        var s = new Setup();
        s.Gateway.Flag = subject =>
        {
            var operation = subject.Attempt.Operation;
            operation = defect switch
            {
                "account" => operation with { Actor = Actor() with { UserId = Id(99) } },
                "profile" => operation with { Actor = Actor() with { ModuleProfileId = Id(99) } },
                "module" => operation with { Actor = Actor(AiModule.Social) },
                "conversation" => operation with { ConversationId = Id(99) },
                _ => operation
            };
            var flag = Flag(subject with { Attempt = subject.Attempt with { Operation = operation } });
            return defect switch
            {
                "attempt" => flag with { Subject = subject with { Attempt = subject.Attempt with { AttemptId = Id(99) } } },
                "message" => flag with { Subject = subject with { MessageId = Id(99) } },
                "revision" => flag with { EvidenceRevision = Guid.Empty },
                "flag" => flag with { FlagId = Id(99) },
                _ => flag
            };
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.RouteValidatedFlagAsync(Subject(), Id(30), Id(25)));
        Assert.Empty(s.Gateway.Routes);
    }

    [Fact]
    public async Task E_SyntheticValidatedFlag_CoordinatesOnlyTheExactRequest()
    {
        var s = new Setup();
        using var cts = new CancellationTokenSource();
        var result = await s.Service.RouteValidatedFlagAsync(Subject(), Id(30), Id(25), cts.Token);
        Assert.Equal(AiReviewAcknowledgementOutcome.Queued, result.Outcome);
        var call = Assert.Single(s.Gateway.Routes);
        Assert.Equal(result.Request, call.Request);
        Assert.Equal(Flag(Subject()), call.Evidence);
        Assert.Equal(cts.Token, call.Token);
        Assert.Equal(2, s.Gate.Calls.Count);
    }

    [Fact]
    public async Task F_Diagnostics_OmitRawTextAndWithholdingDoesNotRoute()
    {
        var s = new Setup();
        const string text = "Synthetic private distress marker";
        s.Gateway.Assessment = request => new(request.Subject, request.ContentDigest, Id(40), AiSafetyOutcome.SafetyResponseRequired);
        var result = await s.Service.AssessInputAsync(Subject(), text);
        Assert.DoesNotContain(text, Assert.Single(s.Gateway.Assessments).Request.ToString());
        Assert.DoesNotContain(text, result.ToString());
        Assert.Empty(s.Gateway.Reports);
        Assert.Empty(s.Gateway.Routes);
        // Reviewer access, sanctions, retention, emergency procedures and classifier accuracy
        // require future M2/M6 integration tests; there are no such capabilities in this service.
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("privacy")]
    [InlineData("assessment")]
    [InlineData("report")]
    [InlineData("flag")]
    [InlineData("route")]
    public async Task G_DependencyFailure_PropagatesOriginalWithoutRetry(string stage)
    {
        var s = new Setup();
        var failure = new InvalidOperationException("Synthetic dependency failure");
        if (stage == "authorization") s.Auth.Callback = _ => throw failure;
        if (stage == "privacy") s.Gate.Callback = _ => throw failure;
        s.Gateway.Callback = (current, _) => { if (current == stage) throw failure; };
        var error = await Record.ExceptionAsync(() => Invoke(s, stage));
        Assert.Same(failure, error);
        Assert.True(s.Gateway.Assessments.Count <= 1 && s.Gateway.Reports.Count <= 1 && s.Gateway.Routes.Count <= 1);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("privacy")]
    [InlineData("assessment")]
    [InlineData("report")]
    [InlineData("flag")]
    [InlineData("route")]
    public async Task G_CancellationAfterDependencyCall_DoesNotGrantOrRetry(string stage)
    {
        var s = new Setup();
        using var cts = new CancellationTokenSource();
        if (stage == "authorization") s.Auth.Callback = _ => cts.Cancel();
        if (stage == "privacy") s.Gate.Callback = _ => cts.Cancel();
        s.Gateway.Callback = (current, token) => { Assert.Equal(cts.Token, token); if (current == stage) cts.Cancel(); };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(s, stage, cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.True(s.Gateway.Assessments.Count <= 1 && s.Gateway.Reports.Count <= 1 && s.Gateway.Routes.Count <= 1);
    }

    [Fact]
    public async Task G_PreCancelled_DoesNotAuthorize()
    {
        var s = new Setup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Service.AssessInputAsync(Subject(), "Hello", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Service.SubmitUserReportAsync(Report(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Service.RouteValidatedFlagAsync(Subject(), Id(30), Id(25), cts.Token));
        Assert.Empty(s.Auth.Calls);
    }

    [Fact]
    public async Task H_RepeatedChecks_ReauthorizeAndBindNewContent()
    {
        var s = new Setup();
        await s.Service.AssessInputAsync(Subject(), "First");
        await s.Service.AssessInputAsync(Subject(), "Changed");
        Assert.NotEqual(s.Gateway.Assessments[0].Request.ContentDigest, s.Gateway.Assessments[1].Request.ContentDigest);
        s.Gateway.Assessment = request => new(request.Subject, request.ContentDigest, Id(40), AiSafetyOutcome.PolicyUnavailable);
        Assert.Equal(AiSafetyOutcome.PolicyUnavailable, (await s.Service.AssessInputAsync(Subject(), "Third")).Outcome);
        s.Auth.Actor = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.AssessInputAsync(Subject(), "Fourth"));
        Assert.Equal(3, s.Gateway.Assessments.Count);
        Assert.Equal(4, s.Auth.Calls.Count);
    }

    [Fact]
    public async Task H_RepeatedReportsAndFlags_ForwardStableIdempotencyReferences()
    {
        var s = new Setup();
        for (var i = 0; i < 2; i++)
        {
            await s.Service.SubmitUserReportAsync(Report());
            await s.Service.RouteValidatedFlagAsync(Subject(), Id(30), Id(25));
        }
        Assert.Equal(s.Gateway.Reports[0].Request, s.Gateway.Reports[1].Request);
        Assert.Equal(s.Gateway.Routes[0].Request, s.Gateway.Routes[1].Request);
        Assert.Equal(6, s.Auth.Calls.Count);
        // Durable case deduplication is a gateway requirement, not an in-memory claim.
    }

    [Fact]
    public async Task E_RevocationBetweenValidationAndRouting_BlocksCaseSubmission()
    {
        var s = new Setup();
        s.Gateway.Callback = (stage, _) => { if (stage == "flag") s.Auth.Actor = null; };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.RouteValidatedFlagAsync(Subject(), Id(30), Id(25)));
        Assert.Single(s.Gateway.Validations);
        Assert.Empty(s.Gateway.Routes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E_MalformedRoutingReceipt_DoesNotClaimSuccess(bool wrongRequest)
    {
        var s = new Setup();
        s.Gateway.Acknowledge = request => wrongRequest ? Ack(request with { TriggerId = Id(99) })
            : new(request, AiReviewAcknowledgementOutcome.Queued);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.RouteValidatedFlagAsync(Subject(), Id(30), Id(25)));
        Assert.Single(s.Gateway.Routes);
    }

    [Fact]
    public async Task G_CancellationWhileAssessmentPending_PropagatesOriginalToken()
    {
        var s = new Setup();
        using var cts = new CancellationTokenSource();
        var pending = new TaskCompletionSource<AiSafetyAssessmentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cts.Token.Register(() => pending.TrySetCanceled(cts.Token));
        s.Gateway.AsyncAssessment = (_, token) =>
        {
            Assert.Equal(cts.Token, token);
            return pending.Task;
        };
        var task = s.Service.AssessInputAsync(Subject(), "Hello", cts.Token);
        Assert.Single(s.Gateway.Assessments);
        cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Single(s.Gateway.Assessments);
    }

    [Fact]
    public async Task H_ChangedSubjectAndAttempt_RequireNewAssessmentBindings()
    {
        var s = new Setup();
        await s.Service.AssessInputAsync(Subject(), "Hello");
        var original = Assert.Single(s.Gateway.Assessments).Request;
        s.Gateway.Assessment = _ => new(original.Subject, original.ContentDigest, Id(40), AiSafetyOutcome.ClearedForFurtherChecks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.AssessInputAsync(Subject() with { MessageId = Id(99) }, "Hello"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.AssessInputAsync(Subject() with { Attempt = Subject().Attempt with { AttemptId = Id(99) } }, "Hello"));
        Assert.Equal(3, s.Gateway.Assessments.Count);
    }

    private static async Task Invoke(Setup s, string stage, CancellationToken token = default)
    {
        if (stage == "report") await s.Service.SubmitUserReportAsync(Report(), token);
        else if (stage is "flag" or "route") await s.Service.RouteValidatedFlagAsync(Subject(), Id(30), Id(25), token);
        else await s.Service.AssessInputAsync(Subject(), "Hello", token);
    }
    private static AiReviewAcknowledgement Ack(AiReviewRequestBinding request, AiReviewAcknowledgementOutcome outcome = AiReviewAcknowledgementOutcome.Queued) =>
        new(request, outcome, outcome is AiReviewAcknowledgementOutcome.Accepted or AiReviewAcknowledgementOutcome.Queued ? Id(41) : null);
    private sealed class Setup
    {
        public Authorization Auth { get; } = new();
        public PrivacyGate Gate { get; } = new();
        public Gateway Gateway { get; } = new();
        public AiPrivacyNoticeGuard Privacy { get; }
        public AiSafetyReviewService Service { get; }
        public Setup()
        {
            Auth.Actor = Actor();
            Gate.Outcome = AiPrivacyNoticeGateOutcome.Satisfied;
            Gateway.Assessment = request => new(request.Subject, request.ContentDigest, Id(40), AiSafetyOutcome.ClearedForFurtherChecks);
            Gateway.Acknowledge = request => Ack(request);
            Gateway.Flag = subject => AiSafetyReviewServiceTests.Flag(subject);
            Privacy = new(Auth, Gate);
            Service = new(Auth, Privacy, Gateway);
        }
    }
    private sealed class Authorization : IAiActorAuthorization
    {
        public AiAuthorizedActor? Actor { get; set; }
        public Action<CancellationToken>? Callback { get; set; }
        public List<(AiModule Module, CancellationToken Token)> Calls { get; } = new();
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default)
        {
            Calls.Add((module, cancellationToken)); Callback?.Invoke(cancellationToken);
            return Task.FromResult(Actor);
        }
    }
    private sealed class PrivacyGate : IAiPrivacyNoticeGate
    {
        public AiPrivacyNoticeGateOutcome Outcome { get; set; } = AiPrivacyNoticeGateOutcome.PolicyNotVerified;
        public Action<CancellationToken>? Callback { get; set; }
        public List<(AiAuthorizedActor Actor, CancellationToken Token)> Calls { get; } = new();
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        {
            Calls.Add((actor, cancellationToken)); Callback?.Invoke(cancellationToken);
            return Task.FromResult(Outcome);
        }
    }
    private sealed class Gateway : IAiSafetyReviewGateway
    {
        public Func<AiSafetyAssessmentRequest, AiSafetyAssessmentResult> Assessment { get; set; } = r => new(r.Subject, r.ContentDigest, Guid.Empty, AiSafetyOutcome.Unknown);
        public Func<AiSafetyAssessmentRequest, CancellationToken, Task<AiSafetyAssessmentResult>>? AsyncAssessment { get; set; }
        public Func<AiReviewRequestBinding, AiReviewAcknowledgement> Acknowledge { get; set; } = r => new(r, AiReviewAcknowledgementOutcome.Unknown);
        public Func<AiSafetySubject, AiValidatedSafetyFlag?> Flag { get; set; } = _ => null;
        public Action<string, CancellationToken>? Callback { get; set; }
        public List<(AiSafetyAssessmentRequest Request, CancellationToken Token)> Assessments { get; } = new();
        public List<(AiReviewRequestBinding Request, CancellationToken Token)> Reports { get; } = new();
        public List<(AiSafetySubject Subject, Guid Flag, CancellationToken Token)> Validations { get; } = new();
        public List<(AiReviewRequestBinding Request, AiValidatedSafetyFlag Evidence, CancellationToken Token)> Routes { get; } = new();
        public Task<AiSafetyAssessmentResult> AssessAsync(AiSafetyAssessmentRequest request, CancellationToken cancellationToken = default)
        {
            Assessments.Add((request, cancellationToken)); Callback?.Invoke("assessment", cancellationToken);
            return AsyncAssessment?.Invoke(request, cancellationToken) ?? Task.FromResult(Assessment(request));
        }
        public Task<AiReviewAcknowledgement> SubmitUserReportAsync(AiReviewRequestBinding request, CancellationToken cancellationToken = default)
        {
            Reports.Add((request, cancellationToken)); Callback?.Invoke("report", cancellationToken);
            return Task.FromResult(Acknowledge(request));
        }
        public Task<AiValidatedSafetyFlag?> ValidateFlagAsync(AiSafetySubject subject, Guid flagId, CancellationToken cancellationToken = default)
        {
            Validations.Add((subject, flagId, cancellationToken)); Callback?.Invoke("flag", cancellationToken);
            return Task.FromResult(Flag(subject));
        }
        public Task<AiReviewAcknowledgement> RouteValidatedFlagAsync(AiReviewRequestBinding request, AiValidatedSafetyFlag evidence, CancellationToken cancellationToken = default)
        {
            Routes.Add((request, evidence, cancellationToken)); Callback?.Invoke("route", cancellationToken);
            return Task.FromResult(Acknowledge(request));
        }
    }
}
