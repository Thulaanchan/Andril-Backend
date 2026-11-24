using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.UnitTests.Hobbies.Xp;

// PROPOSED TEST-ONLY metadata contract. No XP amounts, earning policy, shared engine,
// production authorization, durable storage or grants. Synthetic authority and evidence
// must never be mistaken for trusted production identities or verified source records.
public static class HobbiesXpReceiptContractCandidate
{
    public enum SyntheticAsset { Xp = 1, FlightPoints = 2, Coins = 3 }
    public enum SyntheticEvidenceOutcome { Verified = 1, Missing = 2, Unavailable = 3, Denied = 4 }
    internal readonly record struct SyntheticScope(Guid Owner, string Module, bool IsActive);
    internal readonly record struct CandidateEvent(Guid Owner, string Module, Guid EventId, Guid EvidenceId, SyntheticAsset Asset);
    internal readonly record struct ReceiptEvidence(Guid Owner, string Module, Guid EventId, Guid EvidenceId);
    internal readonly record struct VerificationResult(SyntheticEvidenceOutcome Outcome, CandidateEvent? Event);

    internal sealed record SyntheticXpReceipt
    {
        internal SyntheticXpReceipt(CandidateEvent input)
        {
            Owner = input.Owner;
            Module = input.Module;
            EventId = input.EventId;
            EvidenceId = input.EvidenceId;
        }

        public Guid Owner { get; }
        public string Module { get; }
        public Guid EventId { get; }
        public Guid EvidenceId { get; }
    }

    internal sealed class HobbiesOnlyCandidate
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Module, Guid Owner, Guid Event), SyntheticXpReceipt> _receipts = [];
        internal int ReceiptCount { get { lock (_gate) return _receipts.Count; } }

        private static void RequireScope(SyntheticScope scope)
        {
            if (scope.Owner == Guid.Empty || scope.Module != ModuleNames.Hobbies || !scope.IsActive)
                throw new UnauthorizedAccessException("Synthetic Hobbies scope denied.");
        }

        private static void ValidateEvent(CandidateEvent input)
        {
            if (input.Owner == Guid.Empty || input.Module != ModuleNames.Hobbies || input.Asset != SyntheticAsset.Xp
                || input.EventId == Guid.Empty || input.EvidenceId == Guid.Empty)
                throw new ArgumentException("Invalid synthetic Hobbies XP event.", nameof(input));
        }

        private static void ValidateScopedEvent(SyntheticScope scope, CandidateEvent input)
        {
            RequireScope(scope);
            ValidateEvent(input);
            if (input.Owner != scope.Owner)
                throw new ArgumentException("Invalid synthetic Hobbies XP event.", nameof(input));
        }

        internal SyntheticXpReceipt AcceptSyntheticBackendEvent(SyntheticScope scope, CandidateEvent input)
        {
            ValidateScopedEvent(scope, input);
            lock (_gate) return AcceptLocked(input);
        }

        private SyntheticXpReceipt AcceptLocked(CandidateEvent input)
        {
            var key = (input.Module, input.Owner, input.EventId);
            if (_receipts.TryGetValue(key, out var original))
            {
                if (original.EvidenceId != input.EvidenceId)
                    throw new InvalidOperationException("Synthetic XP event conflict.");
                return original;
            }
            var accepted = new SyntheticXpReceipt(input);
            _receipts.Add(key, accepted);
            return accepted;
        }

        internal async Task<SyntheticXpReceipt?> VerifyAndAcceptAsync(SyntheticScope scope, CandidateEvent input,
            Func<CancellationToken, Task<VerificationResult>> verify, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateScopedEvent(scope, input);
            ArgumentNullException.ThrowIfNull(verify);
            var result = await verify(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Canonical evidence must match every field, including source owner and module.
            // Caller-controlled booleans or a successful lookup alone establish no binding.
            if (result.Outcome != SyntheticEvidenceOutcome.Verified || result.Event != input)
                return null;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return AcceptLocked(input);
            }
        }

        internal SyntheticXpReceipt? FindReceipt(SyntheticScope scope, Guid eventId)
        {
            RequireScope(scope);
            lock (_gate) return _receipts.GetValueOrDefault((scope.Module, scope.Owner, eventId));
        }

        // Trusted-fixture snapshot only; this is not an owner-facing listing or serialization API.
        internal IReadOnlyList<ReceiptEvidence> SnapshotForTrustedRestoration()
        {
            lock (_gate)
                return Array.AsReadOnly(_receipts.Values
                    .Select(r => new ReceiptEvidence(r.Owner, r.Module, r.EventId, r.EvidenceId)).ToArray());
        }

        // Both collections are synthetic fixture inputs. Consistency is not proof of authenticity.
        // Restoration is all-or-nothing and requires one matching receipt per accepted source event.
        internal static HobbiesOnlyCandidate Restore(IReadOnlyCollection<CandidateEvent> trustedAcceptedEvents,
            IReadOnlyCollection<ReceiptEvidence> suppliedReceipts)
        {
            ArgumentNullException.ThrowIfNull(trustedAcceptedEvents);
            ArgumentNullException.ThrowIfNull(suppliedReceipts);
            var restored = new HobbiesOnlyCandidate();
            var expected = new Dictionary<(string Module, Guid Owner, Guid Event), CandidateEvent>();
            foreach (var input in trustedAcceptedEvents)
            {
                ValidateEvent(input);
                if (!expected.TryAdd((input.Module, input.Owner, input.EventId), input))
                    throw new InvalidOperationException("Invalid synthetic restoration evidence.");
            }
            foreach (var receipt in suppliedReceipts)
            {
                var key = (receipt.Module, receipt.Owner, receipt.EventId);
                if (!expected.Remove(key, out var input) || receipt.EvidenceId != input.EvidenceId)
                    throw new InvalidOperationException("Invalid synthetic restoration evidence.");
                restored.AcceptLocked(input);
            }
            if (expected.Count != 0)
                throw new InvalidOperationException("Invalid synthetic restoration evidence.");
            return restored;
        }

        internal SyntheticXpReceipt? AcceptClientClaim(object? ignoredClientClaim) => null;
    }
}
