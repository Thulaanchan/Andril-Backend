using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;
using Xunit;

namespace Andril.UnitTests.Hobbies;

// PROPOSED TEST-ONLY metadata characterization. CF-09 has no XP implementation in
// this checkout. These receipts contain no XP amount, balance, earning trigger,
// level or achievement. They prove no production authorization or durable grant.
public sealed class HobbiesXpSecurityFoundationCharacterizationTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid OtherOwner = Guid.NewGuid();
    private static readonly SyntheticScope Scope = new(Owner, ModuleNames.Hobbies, true);
    private static CandidateEvent Event() => new(Owner, ModuleNames.Hobbies, Guid.NewGuid(), Guid.NewGuid(), SyntheticAsset.Xp);

    [Fact]
    public void Candidate_HobbiesReceiptUsesIndependentProfileAndModuleScope()
    {
        var candidate = new HobbiesOnlyCandidate(); var first = Event();
        var accepted = candidate.AcceptSyntheticBackendEvent(Scope, first);
        var second = first with { Owner = OtherOwner };
        var otherScope = Scope with { Owner = OtherOwner };
        var other = candidate.AcceptSyntheticBackendEvent(otherScope, second);
        Assert.NotSame(accepted, other); Assert.Equal(2, candidate.ReceiptCount);
        Assert.Equal(Owner, accepted.Owner); Assert.Equal(OtherOwner, other.Owner);
        Assert.Equal(ModuleNames.Hobbies, accepted.Module);
        Assert.Same(accepted, candidate.FindReceipt(Scope, first.EventId));
        Assert.Same(other, candidate.FindReceipt(otherScope, first.EventId));
        // Reusing an event ID across synthetic profiles demonstrates candidate scope only.
        // The final CF-09 business-event uniqueness scope remains a shared contract decision.
    }

    [Theory]
    [InlineData(ModuleNames.Learning)]
    [InlineData(ModuleNames.Social)]
    [InlineData("Hobbies")]
    [InlineData("")]
    [InlineData(null)]
    public void Candidate_OtherModuleAuthorityCannotReadOrWriteHobbiesReceipts(string? module)
    {
        var candidate = new HobbiesOnlyCandidate(); var input = Event();
        var original = candidate.AcceptSyntheticBackendEvent(Scope, input);
        var wrong = Scope with { Module = module! };
        Assert.Throws<UnauthorizedAccessException>(() => candidate.AcceptSyntheticBackendEvent(wrong, input));
        Assert.Throws<UnauthorizedAccessException>(() => candidate.FindReceipt(wrong, input.EventId));
        Assert.Same(original, candidate.FindReceipt(Scope, input.EventId)); Assert.Equal(1, candidate.ReceiptCount);
    }

    [Theory]
    [InlineData(ModuleNames.Learning)]
    [InlineData(ModuleNames.Social)]
    public void Candidate_ForeignModuleEventCannotBeRelabelledAsHobbiesXp(string module)
    {
        var candidate = new HobbiesOnlyCandidate();
        Assert.Throws<ArgumentException>(() => candidate.AcceptSyntheticBackendEvent(Scope, Event() with { Module = module }));
        Assert.Equal(0, candidate.ReceiptCount);
    }

    [Fact]
    public void Candidate_ForeignOwnerCannotSubmitOrDiscoverAnotherProfileEvent()
    {
        var candidate = new HobbiesOnlyCandidate(); var input = Event();
        var accepted = candidate.AcceptSyntheticBackendEvent(Scope, input);
        var foreign = Scope with { Owner = OtherOwner };
        var error = Assert.Throws<ArgumentException>(() => candidate.AcceptSyntheticBackendEvent(foreign, input));
        Assert.DoesNotContain(Owner.ToString(), error.Message);
        Assert.Null(candidate.FindReceipt(foreign, input.EventId));
        Assert.Same(accepted, candidate.FindReceipt(Scope, input.EventId)); Assert.Equal(1, candidate.ReceiptCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Candidate_InvalidSyntheticAuthorityCannotCreateReceipt(bool emptyOwner)
    {
        var candidate = new HobbiesOnlyCandidate();
        var denied = emptyOwner ? Scope with { Owner = Guid.Empty } : Scope with { IsActive = false };
        Assert.Throws<UnauthorizedAccessException>(() => candidate.AcceptSyntheticBackendEvent(denied, Event()));
        Assert.Equal(0, candidate.ReceiptCount);
    }

    [Fact]
    public void Candidate_IdenticalEventRetryReturnsOriginalReceiptWithoutAnotherAcceptance()
    {
        var candidate = new HobbiesOnlyCandidate(); var input = Event();
        var first = candidate.AcceptSyntheticBackendEvent(Scope, input);
        Assert.Same(first, candidate.AcceptSyntheticBackendEvent(Scope, input with { }));
        Assert.Equal(1, candidate.ReceiptCount);
        Assert.Equal(input.EvidenceId, first.EvidenceId);
    }

    [Fact]
    public void Candidate_SameEventWithChangedEvidenceConflictsWithoutReplacingOriginal()
    {
        var candidate = new HobbiesOnlyCandidate(); var input = Event();
        var original = candidate.AcceptSyntheticBackendEvent(Scope, input);
        Assert.Throws<InvalidOperationException>(() => candidate.AcceptSyntheticBackendEvent(Scope, input with { EvidenceId = Guid.NewGuid() }));
        Assert.Same(original, candidate.FindReceipt(Scope, input.EventId));
        Assert.Equal(input.EvidenceId, original.EvidenceId); Assert.Equal(1, candidate.ReceiptCount);
    }

    [Fact]
    public async Task Candidate_SynchronizedDuplicateAttemptsHaveOneTestProcessReceipt()
    {
        var candidate = new HobbiesOnlyCandidate(); var input = Event();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            await start.Task;
            return candidate.AcceptSyntheticBackendEvent(Scope, input);
        }).ToArray();
        start.SetResult();
        var receipts = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(receipts, receipt => Assert.Same(receipts[0], receipt));
        Assert.Equal(1, candidate.ReceiptCount);
        // The lock is in this fixture only; no PostgreSQL or cross-process guarantee is asserted.
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("event")]
    [InlineData("evidence")]
    [InlineData("module")]
    [InlineData("default")]
    public void Candidate_InvalidEventsCannotCreateReceipt(string invalid)
    {
        var candidate = new HobbiesOnlyCandidate(); var input = Event();
        input = invalid switch
        {
            "owner" => input with { Owner = Guid.Empty },
            "event" => input with { EventId = Guid.Empty },
            "evidence" => input with { EvidenceId = Guid.Empty },
            "module" => input with { Module = null! },
            _ => default
        };
        Assert.Throws<ArgumentException>(() => candidate.AcceptSyntheticBackendEvent(Scope, input));
        Assert.Equal(0, candidate.ReceiptCount);
    }

    [Theory]
    [InlineData(SyntheticAsset.FlightPoints)]
    [InlineData(SyntheticAsset.Coins)]
    [InlineData((SyntheticAsset)0)]
    public void Candidate_FlightPointsCoinsAndUnknownAssetsCannotEnterXpReceiptStream(SyntheticAsset asset)
    {
        var candidate = new HobbiesOnlyCandidate(); var input = Event();
        var original = candidate.AcceptSyntheticBackendEvent(Scope, input);
        Assert.Throws<ArgumentException>(() => candidate.AcceptSyntheticBackendEvent(Scope, input with { Asset = asset }));
        Assert.Same(original, candidate.FindReceipt(Scope, input.EventId)); Assert.Equal(1, candidate.ReceiptCount);
        // No coin/Flight Points repository, conversion, transfer or earning rule is supplied here.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Candidate_ClientClaimsCannotCreateReceiptEvenWithForgedServerFlags(bool typedEvent)
    {
        var candidate = new HobbiesOnlyCandidate(); var input = Event();
        object claim = typedEvent ? input : new { isServerVerified = true, isAuthorized = true,
            module = ModuleNames.Hobbies, ownerProfileId = Owner, eventId = input.EventId };
        Assert.Null(candidate.AcceptClientClaim(claim)); Assert.Equal(0, candidate.ReceiptCount);
        var original = candidate.AcceptSyntheticBackendEvent(Scope, input);
        Assert.Null(candidate.AcceptClientClaim(claim));
        Assert.Same(original, candidate.FindReceipt(Scope, input.EventId)); Assert.Equal(1, candidate.ReceiptCount);
        // A real protected endpoint and authoritative grant provider are absent, not proven by this fake.
    }

    [Fact]
    public void Candidate_ReceiptContainsOnlyImmutableUnquantifiedEventMetadata()
    {
        var type = typeof(SyntheticXpReceipt);
        Assert.Equal(new[] { "EventId", "EvidenceId", "Module", "Owner" }, type.GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.True(type.IsSealed);
    }

    public enum SyntheticAsset { Xp = 1, FlightPoints = 2, Coins = 3 }
    private readonly record struct SyntheticScope(Guid Owner, string Module, bool IsActive);
    private readonly record struct CandidateEvent(Guid Owner, string Module, Guid EventId, Guid EvidenceId, SyntheticAsset Asset);
    private sealed record SyntheticXpReceipt
    {
        internal SyntheticXpReceipt(CandidateEvent input)
        { Owner = input.Owner; Module = input.Module; EventId = input.EventId; EvidenceId = input.EvidenceId; }
        public Guid Owner { get; }
        public string Module { get; }
        public Guid EventId { get; }
        public Guid EvidenceId { get; }
    }

    // Hobbies consumption candidate only; this does not implement or modify the shared CF-09 engine.
    private sealed class HobbiesOnlyCandidate
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Module, Guid Owner, Guid Event), SyntheticXpReceipt> _receipts = [];
        internal int ReceiptCount { get { lock (_gate) return _receipts.Count; } }
        private static void RequireScope(SyntheticScope scope)
        {
            if (scope.Owner == Guid.Empty || scope.Module != ModuleNames.Hobbies || !scope.IsActive)
                throw new UnauthorizedAccessException("Synthetic Hobbies scope denied.");
        }
        internal SyntheticXpReceipt AcceptSyntheticBackendEvent(SyntheticScope scope, CandidateEvent input)
        {
            RequireScope(scope);
            if (input.Owner != scope.Owner || input.Module != ModuleNames.Hobbies || input.Asset != SyntheticAsset.Xp
                || input.EventId == Guid.Empty || input.EvidenceId == Guid.Empty)
                throw new ArgumentException("Invalid synthetic Hobbies XP event.", nameof(input));
            lock (_gate)
            {
                var key = (input.Module, input.Owner, input.EventId);
                if (_receipts.TryGetValue(key, out var original))
                {
                    if (original.EvidenceId != input.EvidenceId) throw new InvalidOperationException("Synthetic XP event conflict.");
                    return original;
                }
                var accepted = new SyntheticXpReceipt(input); _receipts.Add(key, accepted); return accepted;
            }
        }
        internal SyntheticXpReceipt? FindReceipt(SyntheticScope scope, Guid eventId)
        {
            RequireScope(scope);
            lock (_gate) return _receipts.GetValueOrDefault((scope.Module, scope.Owner, eventId));
        }
        internal SyntheticXpReceipt? AcceptClientClaim(object? ignoredClientClaim) => null;
    }
}
