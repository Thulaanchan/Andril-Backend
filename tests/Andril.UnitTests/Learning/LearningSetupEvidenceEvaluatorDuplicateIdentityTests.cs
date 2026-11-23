using System.Collections;
using System.Text.Json;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Xunit;

namespace Andril.UnitTests.Learning;

/// <summary>Controlled offline contradictory evidence; no real authority, database or freshness guarantee.</summary>
public sealed class LearningSetupEvidenceEvaluatorDuplicateIdentityTests
{
    [Theory]
    [InlineData("different-purpose", false)]
    [InlineData("different-purpose", true)]
    [InlineData("different-skill", false)]
    [InlineData("different-skill", true)]
    [InlineData("different-purpose-and-skill", false)]
    [InlineData("different-purpose-and-skill", true)]
    public void DuplicateSelectionId_WithDistinctValidMemberships_IsUnavailableWithoutMutation(
        string difference, bool reverseOrder)
    {
        var f = new Fixture();
        var earlier = f.Input();
        var earlierState = new InputState(earlier);
        Assert.Equal(LearningSetupEvidenceStatus.Satisfied, LearningSetupEvidenceEvaluator.Evaluate(earlier).Status);
        var original = Assert.Single(f.Selections);
        var kind = difference == "different-skill"
            ? LearningSkillSelectionKind.Learning : LearningSkillSelectionKind.Teaching;
        var skill = difference == "different-purpose" ? f.FirstSkill : f.SecondSkill;
        var contradictory = LearningSkillSelection.CreateFromPreferences(original.Id, f.Preferences, skill, kind);

        Assert.NotSame(original, contradictory);
        Assert.Equal(original.Id, contradictory.Id);
        Assert.NotEqual((original.Kind, original.SkillId), (contradictory.Kind, contradictory.SkillId));
        Assert.Equal(original.LearningProfileId, contradictory.LearningProfileId);
        Assert.Equal("learning", contradictory.Module);
        // Both memberships are valid. The only inconsistent selection-set fact is the repeated row ID.
        if (reverseOrder) f.Selections.Insert(0, contradictory); else f.Selections.Add(contradictory);
        var input = f.Input();
        var inputState = new InputState(input);
        var sourceState = new SourceState(f);

        var result = LearningSetupEvidenceEvaluator.Evaluate(input);

        AssertUnavailableAndPrivate(f, input, result);
        sourceState.AssertUnchanged(f);
        inputState.AssertUnchanged(input);
        earlierState.AssertUnchanged(earlier);
        Assert.Single(earlier.Selections!);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DuplicateGoalId_WithIdenticalOrConflictingText_IsUnavailableWithoutMutation(
        bool conflictingText, bool reverseOrder)
    {
        var f = new Fixture();
        var earlier = f.Input();
        var earlierState = new InputState(earlier);
        Assert.Equal(LearningSetupEvidenceStatus.Satisfied, LearningSetupEvidenceEvaluator.Evaluate(earlier).Status);
        var original = Assert.Single(f.Goals);
        if (conflictingText)
            f.Preferences.EditGoal(original.Id, "Changed private simulated goal தமிழ் සිංහල e\u0301");
        var contradictory = LearningPersonalGoal.CreateFromPreferences(f.Preferences, original.Id);

        Assert.NotSame(original, contradictory);
        Assert.Equal(original.Id, contradictory.Id);
        Assert.Equal(original.LearningProfileId, contradictory.LearningProfileId);
        Assert.Equal("learning", contradictory.Module);
        Assert.Equal(conflictingText, original.Text != contradictory.Text);
        // Distinct goal IDs with equal text are already covered as valid; this tests repeated identity.
        if (reverseOrder) f.Goals.Insert(0, contradictory); else f.Goals.Add(contradictory);
        var input = f.Input();
        var inputState = new InputState(input);
        var sourceState = new SourceState(f);

        var result = LearningSetupEvidenceEvaluator.Evaluate(input);

        AssertUnavailableAndPrivate(f, input, result);
        sourceState.AssertUnchanged(f);
        inputState.AssertUnchanged(input);
        earlierState.AssertUnchanged(earlier);
        Assert.Single(earlier.Goals!);
        Assert.Equal(original.Text, earlier.Goals![0]!.Text);
    }

    private static void AssertUnavailableAndPrivate(Fixture f, LearningSetupEvidenceSimulation input,
        LearningSetupEvidenceResult result)
    {
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable, result.Status);
        Assert.NotEqual(LearningSetupEvidenceStatus.Satisfied, result.Status);
        Assert.NotEqual(LearningSetupEvidenceStatus.Incomplete, result.Status);
        var diagnostics = string.Join("\n", new[] { input.ToString(), result.ToString(), f.Catalogue.ToString() }
            .Concat(f.Selections.Select(row => row.ToString()))
            .Concat(f.Goals.Select(row => row.ToString()))
            .Concat(input.Selections!.Select(row => row!.ToString()))
            .Concat(input.Goals!.Select(row => row!.ToString())));
        var ids = new[] { f.Preferences.AccountId, f.Preferences.LearningProfileId, f.FirstSkill, f.SecondSkill }
            .Concat(f.Selections.Select(row => row.Id)).Concat(f.Goals.Select(row => row.Id));
        foreach (var id in ids)
        {
            Assert.True(!diagnostics.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase),
                "Normal diagnostics must omit private evidence identifiers.");
            Assert.True(!diagnostics.Contains(id.ToString("N"), StringComparison.OrdinalIgnoreCase),
                "Normal diagnostics must omit compact private identifiers.");
        }
        foreach (var text in f.Goals.Select(row => row.Text).Append(f.Catalogue.Revision!))
            Assert.True(!diagnostics.Contains(text, StringComparison.Ordinal),
                "Normal diagnostics must omit private content and revision bindings.");
        Assert.Equal("{}", JsonSerializer.Serialize((object)input, input.GetType(), new JsonSerializerOptions()));
        Assert.Equal("{}", JsonSerializer.Serialize((object)result, result.GetType(), new JsonSerializerOptions()));
    }

    private sealed record SelectionFacts(Guid Id, Guid Profile, string Module, Guid Skill,
        LearningSkillSelectionKind Kind);
    private sealed record GoalFacts(Guid Id, Guid Profile, string Module, string Text);

    private static SelectionFacts Facts(LearningSkillSelection row) =>
        new(row.Id, row.LearningProfileId, row.Module, row.SkillId, row.Kind);
    private static GoalFacts Facts(LearningPersonalGoal row) =>
        new(row.Id, row.LearningProfileId, row.Module, row.Text);
    private static SelectionFacts SnapshotFacts(LearningSetupEvidenceSimulation.SelectionSnapshot? row)
    {
        var value = Assert.IsType<LearningSetupEvidenceSimulation.SelectionSnapshot>(row);
        return new(value.Id, value.ProfileId, value.Module, value.SkillId, value.Kind);
    }
    private static GoalFacts SnapshotFacts(LearningSetupEvidenceSimulation.GoalSnapshot? row)
    {
        var value = Assert.IsType<LearningSetupEvidenceSimulation.GoalSnapshot>(row);
        return new(value.Id, value.ProfileId, value.Module, value.Text);
    }

    private sealed class InputState(LearningSetupEvidenceSimulation input)
    {
        private readonly Guid profile = input.LearningProfileId;
        private readonly string? module = input.Module;
        private readonly LearningSetupSimulationPreconditions preconditions = input.Preconditions;
        private readonly LearningSkillCatalogueReadResult? catalogue = input.CatalogueEvidence;
        private readonly IReadOnlyList<LearningSetupEvidenceSimulation.SelectionSnapshot?> selections = input.Selections!;
        private readonly IReadOnlyList<LearningSetupEvidenceSimulation.GoalSnapshot?> goals = input.Goals!;
        private readonly SelectionFacts[] selectionValues = input.Selections!.Select(SnapshotFacts).ToArray();
        private readonly GoalFacts[] goalValues = input.Goals!.Select(SnapshotFacts).ToArray();

        internal void AssertUnchanged(LearningSetupEvidenceSimulation current)
        {
            Assert.Equal(profile, current.LearningProfileId);
            Assert.Equal(module, current.Module);
            Assert.Equal(preconditions, current.Preconditions);
            Assert.Same(catalogue, current.CatalogueEvidence);
            Assert.Same(selections, current.Selections);
            Assert.Same(goals, current.Goals);
            Assert.Equal(selectionValues, current.Selections!.Select(SnapshotFacts).ToArray());
            Assert.Equal(goalValues, current.Goals!.Select(SnapshotFacts).ToArray());
        }
    }

    private sealed class SourceState(Fixture f)
    {
        private readonly LearningSkillSelection[] selections = f.Selections.ToArray();
        private readonly LearningPersonalGoal[] goals = f.Goals.ToArray();
        private readonly SelectionFacts[] selectionValues = f.Selections.Select(Facts).ToArray();
        private readonly GoalFacts[] goalValues = f.Goals.Select(Facts).ToArray();
        private readonly Guid account = f.Preferences.AccountId;
        private readonly Guid profile = f.Preferences.LearningProfileId;
        private readonly IReadOnlyList<Guid> interests = f.Preferences.Interests;
        private readonly IReadOnlyList<Guid> teaching = f.Preferences.TeachingSkills;
        private readonly IReadOnlyList<Guid> learning = f.Preferences.LearningSkills;
        private readonly IReadOnlyList<LearningPreferences.PersonalGoal> preferenceGoals = f.Preferences.Goals;
        private readonly (Guid Id, string Text)[] preferenceGoalValues =
            f.Preferences.Goals.Select(goal => (goal.Id, goal.Text)).ToArray();
        private readonly int selectionReads = f.SelectionRows.EnumerationCount;
        private readonly int goalReads = f.GoalRows.EnumerationCount;
        private readonly string? revision = f.Catalogue.Revision;

        internal void AssertUnchanged(Fixture current)
        {
            Assert.Equal(selections, current.Selections.ToArray());
            Assert.Equal(goals, current.Goals.ToArray());
            Assert.Equal(selectionValues, current.Selections.Select(Facts).ToArray());
            Assert.Equal(goalValues, current.Goals.Select(Facts).ToArray());
            Assert.Equal(account, current.Preferences.AccountId);
            Assert.Equal(profile, current.Preferences.LearningProfileId);
            Assert.Equal("learning", current.Preferences.Module);
            Assert.Same(interests, current.Preferences.Interests);
            Assert.Same(teaching, current.Preferences.TeachingSkills);
            Assert.Same(learning, current.Preferences.LearningSkills);
            Assert.Same(preferenceGoals, current.Preferences.Goals);
            Assert.Equal(preferenceGoalValues, current.Preferences.Goals.Select(goal => (goal.Id, goal.Text)).ToArray());
            Assert.Equal(selectionReads, current.SelectionRows.EnumerationCount);
            Assert.Equal(goalReads, current.GoalRows.EnumerationCount);
            Assert.Equal(revision, current.Catalogue.Revision);
        }
    }

    private sealed class CountedRows<T>(List<T> rows) : IEnumerable<T>
    {
        internal int EnumerationCount { get; private set; }
        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            return rows.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Fixture
    {
        internal Guid FirstSkill { get; } = Guid.Parse("10000000-0000-0000-0000-000000000001");
        internal Guid SecondSkill { get; } = Guid.Parse("10000000-0000-0000-0000-000000000002");
        internal LearningPreferences Preferences { get; } = new(
            Guid.Parse("10000000-0000-0000-0000-000000000003"),
            Guid.Parse("10000000-0000-0000-0000-000000000004"));
        internal List<LearningSkillSelection> Selections { get; } = [];
        internal List<LearningPersonalGoal> Goals { get; } = [];
        internal CountedRows<LearningSkillSelection> SelectionRows { get; }
        internal CountedRows<LearningPersonalGoal> GoalRows { get; }
        internal LearningSkillCatalogueReadResult Catalogue { get; }

        internal Fixture()
        {
            Preferences.UpdateSelections([], [FirstSkill, SecondSkill], [FirstSkill, SecondSkill]);
            Selections.Add(LearningSkillSelection.CreateFromPreferences(
                Guid.Parse("10000000-0000-0000-0000-000000000005"), Preferences, FirstSkill,
                LearningSkillSelectionKind.Learning));
            var goal = Preferences.CreateGoal("Original private simulated goal தமிழ் සිංහල e\u0301");
            Goals.Add(LearningPersonalGoal.CreateFromPreferences(Preferences, goal.Id));
            SelectionRows = new(Selections);
            GoalRows = new(Goals);
            Catalogue = LearningSkillCatalogueReadResult.Available(new LearningSkillCatalogue([
                new LearningSkillCatalogue.Skill(FirstSkill, true, LearningSkillCatalogueStatus.Active),
                new LearningSkillCatalogue.Skill(SecondSkill, true, LearningSkillCatalogueStatus.Active)]),
                "private-simulated-revision-not-authority");
        }

        internal LearningSetupEvidenceSimulation Input() => new(Preferences.LearningProfileId, "learning",
            LearningSetupSimulationPreconditions.Established, SelectionRows, GoalRows, Catalogue);
    }
}
