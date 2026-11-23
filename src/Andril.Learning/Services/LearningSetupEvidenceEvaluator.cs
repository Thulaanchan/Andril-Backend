using System.Text.Json.Serialization;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Andril.UnitTests")]

namespace Andril.Learning.Services;

internal enum LearningSetupEvidenceStatus { Satisfied = 1, Incomplete = 2, Unavailable = 3 }
internal enum LearningSetupSimulationPreconditions { Unverified = 0, Established = 1, Stale = 2, Inconsistent = 3 }

/// <summary>Offline result, never an onboarding or module-access grant.</summary>
internal sealed class LearningSetupEvidenceResult(LearningSetupEvidenceStatus status)
{
    [JsonIgnore]
    public LearningSetupEvidenceStatus Status { get; } = status;
    public override string ToString() => "Learning setup simulation result [private evidence omitted]";
}

/// <summary>
/// Controlled OFFLINE inputs only. Established simulates prior trusted verification of an
/// authorized active Learning profile, complete persisted reads and consistent/current evidence.
/// Neither this enum, a profile ID nor a catalogue revision proves those facts. Do not bind
/// requests to this type or register it in production. M2 scope and authoritative adapters remain
/// pending. Scalar copies isolate this fixture from caller collections and ORM entity changes.
/// </summary>
internal sealed class LearningSetupEvidenceSimulation
{
    internal Guid LearningProfileId { get; }
    internal string? Module { get; }
    internal LearningSetupSimulationPreconditions Preconditions { get; }
    internal LearningSkillCatalogueReadResult? CatalogueEvidence { get; }
    internal IReadOnlyList<SelectionSnapshot?>? Selections { get; }
    internal IReadOnlyList<GoalSnapshot?>? Goals { get; }

    internal LearningSetupEvidenceSimulation(Guid learningProfileId, string? module,
        LearningSetupSimulationPreconditions preconditions,
        IEnumerable<LearningSkillSelection>? selections, IEnumerable<LearningPersonalGoal>? goals,
        LearningSkillCatalogueReadResult? catalogueEvidence)
    {
        LearningProfileId = learningProfileId;
        Module = module;
        Preconditions = preconditions;
        CatalogueEvidence = catalogueEvidence;
        Selections = selections is null ? null : Array.AsReadOnly(selections
            .Select(row => row is null ? null : new SelectionSnapshot(row)).ToArray());
        Goals = goals is null ? null : Array.AsReadOnly(goals
            .Select(row => row is null ? null : new GoalSnapshot(row)).ToArray());
    }

    internal sealed class SelectionSnapshot
    {
        internal Guid Id { get; }
        internal Guid ProfileId { get; }
        internal string Module { get; }
        internal Guid SkillId { get; }
        internal LearningSkillSelectionKind Kind { get; }
        internal SelectionSnapshot(LearningSkillSelection row)
        {
            Id = row.Id; ProfileId = row.LearningProfileId; Module = row.Module;
            SkillId = row.SkillId; Kind = row.Kind;
        }
        public override string ToString() => "Simulated Learning selection [private evidence omitted]";
    }

    internal sealed class GoalSnapshot
    {
        internal Guid Id { get; }
        internal Guid ProfileId { get; }
        internal string Module { get; }
        internal string Text { get; }
        internal GoalSnapshot(LearningPersonalGoal row)
        {
            Id = row.Id; ProfileId = row.LearningProfileId; Module = row.Module; Text = row.Text;
        }
        public override string ToString() => "Simulated Learning goal [private evidence omitted]";
    }

    public override string ToString() => "Learning setup simulation [private evidence omitted; no authority]";
}

/// <summary>
/// Pure, synchronous, UNREGISTERED prototype for LRN-PD-06/07. No readers, database, clock,
/// authentication, active/revoked-profile implementation, writes, retries, XP or connections.
/// Satisfied is conditional on simulated trusted preconditions, not real authorization evidence.
/// Future integration must independently establish ownership, activity, consistency and freshness.
/// No catalogue revision string is treated as authority; no shared onboarding interface is added.
/// </summary>
internal static class LearningSetupEvidenceEvaluator
{
    internal static LearningSetupEvidenceResult Evaluate(LearningSetupEvidenceSimulation? input,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input is null || input.Preconditions != LearningSetupSimulationPreconditions.Established ||
            input.LearningProfileId == Guid.Empty || input.Module != "learning" ||
            input.Selections is null || input.Goals is null ||
            input.CatalogueEvidence is not { State: LearningSkillCatalogueReadState.Available, Catalogue: { } catalogue })
            return new(LearningSetupEvidenceStatus.Unavailable);

        // Inspect ALL scoped rows before evaluating existential requirements. A valid first row
        // must not hide a later foreign, malformed or inconsistent row. No source rows are changed.
        var selectionIds = new HashSet<Guid>();
        var memberships = new HashSet<(LearningSkillSelectionKind, Guid)>();
        foreach (var row in input.Selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row is null || row.Id == Guid.Empty || row.ProfileId != input.LearningProfileId ||
                row.Module != "learning" || row.SkillId == Guid.Empty || !Enum.IsDefined(row.Kind) ||
                !selectionIds.Add(row.Id) || !memberships.Add((row.Kind, row.SkillId)))
                return new(LearningSetupEvidenceStatus.Unavailable);
        }
        var goalIds = new HashSet<Guid>();
        foreach (var row in input.Goals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row is null || row.Id == Guid.Empty || row.ProfileId != input.LearningProfileId ||
                row.Module != "learning" || string.IsNullOrWhiteSpace(row.Text) || !goalIds.Add(row.Id))
                return new(LearningSetupEvidenceStatus.Unavailable);
        }

        var eligibleLearningSkill = false;
        foreach (var row in input.Selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row!.Kind != LearningSkillSelectionKind.Learning) continue;
            try
            {
                // Reuse the approved catalogue rule rather than copying approval/lifecycle logic.
                catalogue.ValidateNewSelections([], [row.SkillId]);
                eligibleLearningSkill = true;
                break;
            }
            catch (ArgumentException)
            {
                // Expected catalogue rejection: historical references stay intact and do not count.
                // Unexpected exceptions are not swallowed or translated into fabricated evidence.
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(eligibleLearningSkill && input.Goals.Count > 0
            ? LearningSetupEvidenceStatus.Satisfied : LearningSetupEvidenceStatus.Incomplete);
    }
}
