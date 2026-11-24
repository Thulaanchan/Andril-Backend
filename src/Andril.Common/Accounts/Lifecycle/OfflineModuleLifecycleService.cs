using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Lifecycle;

internal sealed record ModuleLifecycleCommand(string Module, Guid RequestKey, bool SocialOptIn, long? ExpectedRevision = null,
    ModuleLifecycleOperation? Operation = null, ModuleMembershipScope? TargetScope = null);
internal enum ModuleLifecycleOperation { Activate, Reactivate, Deactivate }

internal sealed record ModuleLifecycleResult(string Module, long Revision, DateTimeOffset StateChangedAtUtc)
{
    public string State { get; init; } = "active";
}

internal sealed record StoredModuleMembership(ModuleMembership Membership, DateTimeOffset StateChangedAtUtc,
    DateTimeOffset? FirstActivatedAtUtc = null);
internal sealed record ModuleLifecycleReceipt(ModuleMembershipScope Scope, Guid RequestKey, ModuleLifecycleOperation Operation,
    long? ExpectedRevision, string IntentHash, ModuleLifecycleResult Result, DateTimeOffset RecordedAtUtc);
internal sealed record ModuleLifecycleAudit(ModuleMembershipScope Scope, ModuleLifecycleOperation Operation,
    string? PreviousState, ModuleLifecycleResult Result, Guid CorrelationId);

internal sealed record ModuleLifecycleSnapshot(ModuleActivationEvidence Evidence, StoredModuleMembership? Current,
    ModuleLifecycleReceipt? Receipt, ModuleDeactivationEvidence? DeactivationEvidence = null);

// A changed membership and its audit are both present, or both absent for a no-op.
// A replay of an existing receipt requires no write at all.
internal sealed record ModuleLifecycleWrite(StoredModuleMembership? ChangedMembership, ModuleLifecycleReceipt Receipt,
    ModuleLifecycleAudit? Audit);
internal sealed record ModuleLifecycleDecision(ModuleLifecycleResult Result, ModuleLifecycleWrite? Write = null);

// Offline boundary. An approved adapter must resolve the account from subject, scope membership
// and receipt lookup by account/module (and receipt by request key), supply fresh evidence inside
// the write boundary, and commit the entire write atomically with revision checks. Returning from
// WriteAsync means a known outcome. ReadAsync is read-only and must also supply current evidence.
// No production adapter or registration is provided here.
internal interface IModuleLifecycleStore
{
    Task<ModuleLifecycleResult> WriteAsync(Guid subject, string module, Guid requestKey,
        Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken cancellationToken);
    Task<ModuleLifecycleSnapshot> ReadAsync(Guid subject, string module, Guid requestKey, CancellationToken cancellationToken);
}

// Explicit capability for offline or verified controlled PostgreSQL implementations.
// Implementing it does not register a production writer or supply authorization evidence.
internal interface IOfflineModuleDeactivationStore : IModuleLifecycleStore;

// Raised only when the store cannot determine whether its commit took effect.
internal sealed class ModuleLifecycleCommitUnknownException : Exception;

internal sealed class OfflineModuleLifecycleService(IModuleLifecycleStore store)
{
    internal async Task<ModuleLifecycleResult> ExecuteAsync(Guid verifiedSubject, ModuleLifecycleCommand command,
        Guid correlationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (verifiedSubject == Guid.Empty)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.UnconfirmedAccount);
        if (!ModuleNames.IsValid(command.Module))
            throw new ModuleLifecycleException(ModuleLifecycleDenial.InvalidScope);
        if (command.RequestKey == Guid.Empty || correlationId == Guid.Empty)
            throw new ArgumentException("The command requires a request key and a correlation ID.");
        var operation = command.Operation ?? (command.ExpectedRevision.HasValue
            ? ModuleLifecycleOperation.Reactivate : ModuleLifecycleOperation.Activate);
        if (!Enum.IsDefined(operation)) throw new ArgumentException("Unknown lifecycle operation.");
        if (operation == ModuleLifecycleOperation.Deactivate)
        {
            if (store is not IOfflineModuleDeactivationStore)
                throw new ModuleLifecycleException(ModuleLifecycleDenial.EvidenceUnavailable);
            if (command.TargetScope is not { } target || target.AccountId == Guid.Empty || target.Module != command.Module)
                throw new ModuleLifecycleException(ModuleLifecycleDenial.InvalidScope);
            if (command.ExpectedRevision is not > 0 or long.MaxValue || command.SocialOptIn)
                throw new ArgumentException("Deactivation requires a bounded expected revision and no activation opt-in.");
        }
        else if ((operation == ModuleLifecycleOperation.Activate && command.ExpectedRevision.HasValue)
            || (operation == ModuleLifecycleOperation.Reactivate && !command.ExpectedRevision.HasValue))
            throw new ArgumentException("Operation and expected revision disagree.");
        if (operation != ModuleLifecycleOperation.Deactivate && (command.Module == ModuleNames.Social) != command.SocialOptIn)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.InvalidOptIn);

        var revision = command.ExpectedRevision?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var normalizedIntent = operation == ModuleLifecycleOperation.Deactivate
            ? $"v2\n{command.TargetScope!.Value.AccountId:D}\n{command.Module}\n{operation}\n{revision}\nFalse"
            : $"v1\n{command.Module}\n{operation}\n{revision}\n{command.SocialOptIn}";
        var intentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedIntent)));

        try
        {
            return await store.WriteAsync(verifiedSubject, command.Module, command.RequestKey,
                snapshot => Decide(snapshot, verifiedSubject, command, operation, intentHash, correlationId),
                cancellationToken);
        }
        catch (ModuleLifecycleCommitUnknownException)
        {
            // Do not start a second write after an ambiguous commit, even when no receipt is found.
            if (cancellationToken.IsCancellationRequested)
                throw;

            ModuleLifecycleSnapshot snapshot;
            try
            {
                snapshot = await store.ReadAsync(verifiedSubject, command.Module, command.RequestKey, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new ModuleLifecycleCommitUnknownException();
            }

            if (snapshot.Receipt is null)
                throw;
            return Decide(snapshot, verifiedSubject, command, operation, intentHash, correlationId).Result;
        }
    }

    private static ModuleLifecycleDecision Decide(ModuleLifecycleSnapshot snapshot, Guid verifiedSubject,
        ModuleLifecycleCommand command, ModuleLifecycleOperation operation, string intentHash, Guid correlationId)
    {
        if (operation == ModuleLifecycleOperation.Deactivate)
            return DecideDeactivation(snapshot, verifiedSubject, command, intentHash, correlationId);
        var evidence = snapshot.Evidence;
        if (evidence.SourceOutcome == ModuleAuthorizationOutcome.Satisfied && evidence.ConfirmedSubject != verifiedSubject)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.UnconfirmedAccount);

        var current = snapshot.Current?.Membership;
        var scope = new ModuleMembershipScope(evidence.Scope.AccountId, command.Module);
        var active = command.ExpectedRevision is long expected
            ? ActiveModuleMembership.Reactivate(scope, evidence, current, expected)
            : ActiveModuleMembership.Activate(scope, evidence, current);

        var changed = !ReferenceEquals(current, active);
        var now = evidence.EvaluatedAtUtc.ToUniversalTime();
        var result = new ModuleLifecycleResult(scope.Module, active.Revision,
            changed ? now : snapshot.Current!.StateChangedAtUtc);

        if (snapshot.Receipt is { } existing)
        {
            if (changed || existing.Scope != scope || existing.RequestKey != command.RequestKey || existing.Operation != operation
                || existing.ExpectedRevision != command.ExpectedRevision || existing.IntentHash != intentHash || existing.Result != result)
                throw new ModuleLifecycleException(ModuleLifecycleDenial.ConflictingTransition);
            return new(result);
        }

        var receipt = new ModuleLifecycleReceipt(scope, command.RequestKey, operation, command.ExpectedRevision, intentHash, result, now);
        return changed
            ? new(result, new(new(active, now, snapshot.Current is null ? now : snapshot.Current.FirstActivatedAtUtc), receipt,
                new(scope, operation, current is null ? null : "inactive", result, correlationId)))
            : new(result, new(null, receipt, null));
    }

    private static ModuleLifecycleDecision DecideDeactivation(ModuleLifecycleSnapshot snapshot, Guid subject,
        ModuleLifecycleCommand command, string intentHash, Guid correlationId)
    {
        var scope = command.TargetScope!.Value;
        var evidence = snapshot.DeactivationEvidence
            ?? throw new ModuleLifecycleException(ModuleLifecycleDenial.EvidenceUnavailable);
        ModuleDeactivation.Validate(scope, subject, evidence, snapshot.Current?.Membership);
        if (snapshot.Receipt is { } receipt)
        {
            if (receipt.Scope != scope || receipt.RequestKey != command.RequestKey
                || receipt.Operation != ModuleLifecycleOperation.Deactivate || receipt.ExpectedRevision != command.ExpectedRevision
                || receipt.IntentHash != intentHash || receipt.Result.Module != scope.Module || receipt.Result.State != "inactive"
                || receipt.Result.Revision != command.ExpectedRevision!.Value + 1
                || snapshot.Current?.Membership is not InactiveModuleMembership current
                || current.Revision != receipt.Result.Revision || snapshot.Current.StateChangedAtUtc != receipt.Result.StateChangedAtUtc)
                throw new ModuleLifecycleException(ModuleLifecycleDenial.ConflictingTransition);
            return new(receipt.Result);
        }
        var inactive = ModuleDeactivation.Deactivate(scope, subject, evidence, snapshot.Current?.Membership,
            command.ExpectedRevision!.Value);
        var now = evidence.EvaluatedAtUtc.ToUniversalTime();
        if (now < snapshot.Current!.StateChangedAtUtc)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.StaleEvidence);
        var result = new ModuleLifecycleResult(scope.Module, inactive.Revision, now) { State = "inactive" };
        var recorded = new ModuleLifecycleReceipt(scope, command.RequestKey, ModuleLifecycleOperation.Deactivate,
            command.ExpectedRevision, intentHash, result, now);
        return new(result, new(new(inactive, now, snapshot.Current.FirstActivatedAtUtc), recorded,
            new(scope, ModuleLifecycleOperation.Deactivate, "active", result, correlationId)));
    }
}
