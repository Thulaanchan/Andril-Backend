using System.Text.Json;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Xunit;
using Skill = Andril.Learning.Entities.LearningSkillCatalogue.Skill;

namespace Andril.UnitTests.Learning;

/// <summary>
/// Simulated trust/currentness and in-memory rows ONLY. No real M2 authorization, active-profile
/// determination, authoritative catalogue loading, RLS, persistence or transaction safety is proven.
/// </summary>
public sealed class LearningSetupEvidenceEvaluatorTests
{
    private sealed class Fixture
    {
        internal LearningPreferences Preferences { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        internal Guid SkillId { get; } = Guid.NewGuid();
        internal List<LearningSkillSelection> Selections { get; } = [];
        internal List<LearningPersonalGoal> Goals { get; } = [];
        internal Fixture()
        {
            Preferences.UpdateSelections([Guid.NewGuid()], [SkillId], [SkillId]);
            Selections.Add(LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), Preferences, SkillId,
                LearningSkillSelectionKind.Teaching));
            Selections.Add(LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), Preferences, SkillId,
                LearningSkillSelectionKind.Learning));
            var goal = Preferences.CreateGoal("Private simulated goal");
            Goals.Add(LearningPersonalGoal.CreateFromPreferences(Preferences, goal.Id));
        }
        internal LearningSkillCatalogueReadResult Catalogue(bool approved = true,
            LearningSkillCatalogueStatus status = LearningSkillCatalogueStatus.Active) =>
            LearningSkillCatalogueReadResult.Available(new LearningSkillCatalogue([new Skill(SkillId, approved, status)]),
                "opaque-simulated-revision");
        internal LearningSetupEvidenceSimulation Input(LearningSkillCatalogueReadResult? catalogue,
            LearningSetupSimulationPreconditions preconditions = LearningSetupSimulationPreconditions.Established,
            string? module = "learning", Guid? profile = null) =>
            new(profile ?? Preferences.LearningProfileId, module, preconditions, Selections, Goals, catalogue);
    }

    [Fact]
    public void EligibleLearningSkillAndGoal_SatisfyOnlyUnderSimulatedTrustedConditions()
    {
        var f = new Fixture();
        Assert.Equal(LearningSetupEvidenceStatus.Satisfied,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue())).Status);
        // Teaching/Learning share an ID; within-list duplicate rules remain independent.
        Assert.Collection(f.Selections,
            row => Assert.Equal(f.SkillId, row.SkillId), row => Assert.Equal(f.SkillId, row.SkillId));
    }

    [Theory]
    [InlineData("teaching-only")]
    [InlineData("no-selections")]
    [InlineData("no-goals")]
    public void KnownMissingRequirement_IsIncomplete(string missing)
    {
        var f = new Fixture();
        if (missing == "teaching-only") f.Selections.RemoveAll(row => row.Kind == LearningSkillSelectionKind.Learning);
        else if (missing == "no-selections") f.Selections.Clear();
        else f.Goals.Clear();
        Assert.Equal(LearningSetupEvidenceStatus.Incomplete,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue())).Status);
    }

    [Theory]
    [InlineData(false, LearningSkillCatalogueStatus.Active)]
    [InlineData(true, LearningSkillCatalogueStatus.Retired)]
    [InlineData(true, LearningSkillCatalogueStatus.Merged)]
    [InlineData(true, LearningSkillCatalogueStatus.Superseded)]
    [InlineData(false, LearningSkillCatalogueStatus.Retired)]
    [InlineData(false, LearningSkillCatalogueStatus.Merged)]
    [InlineData(false, LearningSkillCatalogueStatus.Superseded)]
    public void IneligibleFinalSkill_IsIncompleteAndHistoricalReferencesRemain(bool approved, LearningSkillCatalogueStatus status)
    {
        var f = new Fixture();
        var identities = f.Selections.Select(row => row.Id).ToArray();
        var learning = f.Preferences.LearningSkills;
        Assert.Equal(LearningSetupEvidenceStatus.Incomplete,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue(approved, status))).Status);
        Assert.Equal(identities, f.Selections.Select(row => row.Id));
        Assert.All(f.Selections, row => Assert.Equal(f.SkillId, row.SkillId));
        Assert.Same(learning, f.Preferences.LearningSkills);
    }

    [Fact]
    public void VerifiedEmptyCatalogue_MakesUnknownSkillIncompleteRatherThanUnavailable()
    {
        var f = new Fixture();
        var empty = LearningSkillCatalogueReadResult.Available(new LearningSkillCatalogue([]), "empty-simulated-revision");
        Assert.Equal(LearningSetupEvidenceStatus.Incomplete,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(empty)).Status);
        Assert.Collection(f.Selections,
            row => Assert.Equal(f.SkillId, row.SkillId), row => Assert.Equal(f.SkillId, row.SkillId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrUnavailableCatalogue_IsUnavailable(bool missing)
    {
        var f = new Fixture();
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable, LearningSetupEvidenceEvaluator.Evaluate(
            f.Input(missing ? null : LearningSkillCatalogueReadResult.Unavailable())).Status);
    }

    [Theory]
    [InlineData((int)LearningSetupSimulationPreconditions.Unverified)]
    [InlineData((int)LearningSetupSimulationPreconditions.Stale)]
    [InlineData((int)LearningSetupSimulationPreconditions.Inconsistent)]
    [InlineData(99)]
    public void UnestablishedPreconditions_CannotBeOverriddenByProfileIdOrRevision(int state)
    {
        var f = new Fixture();
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue(), (LearningSetupSimulationPreconditions)state)).Status);
    }

    [Fact]
    public void MissingInput_IsUnavailable() =>
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable, LearningSetupEvidenceEvaluator.Evaluate(null).Status);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingReadSets_AreUnavailableRatherThanKnownEmpty(bool missingSelections)
    {
        var f = new Fixture();
        var input = new LearningSetupEvidenceSimulation(f.Preferences.LearningProfileId, "learning",
            LearningSetupSimulationPreconditions.Established, missingSelections ? null : f.Selections,
            missingSelections ? f.Goals : null, f.Catalogue());
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable, LearningSetupEvidenceEvaluator.Evaluate(input).Status);
    }

    [Theory]
    [InlineData("hobbies")]
    [InlineData("social")]
    [InlineData(null)]
    public void OtherOrMissingModuleScope_CannotSatisfyLearning(string? module)
    {
        var f = new Fixture();
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue(), module: module)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyOrMismatchedProfileScope_IsUnavailable(bool empty)
    {
        var f = new Fixture();
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable, LearningSetupEvidenceEvaluator.Evaluate(
            f.Input(f.Catalogue(), profile: empty ? Guid.Empty : Guid.NewGuid())).Status);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("teaching")]
    [InlineData("goal")]
    public void ValidFirstRows_DoNotHideLaterForeignProfileEvidence(string kind)
    {
        var f = new Fixture();
        var foreign = new Fixture();
        if (kind == "goal") f.Goals.Add(foreign.Goals[0]);
        else f.Selections.Add(foreign.Selections.Single(row => row.Kind ==
            (kind == "learning" ? LearningSkillSelectionKind.Learning : LearningSkillSelectionKind.Teaching)));
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue())).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullRows_AreUnavailable(bool selection)
    {
        var f = new Fixture();
        if (selection) f.Selections.Add(null!); else f.Goals.Add(null!);
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue())).Status);
    }

    [Fact]
    public void DuplicateWithinListEvidence_IsInconsistentWithoutRejectingCrossListOverlap()
    {
        var f = new Fixture();
        f.Selections.Add(LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), f.Preferences, f.SkillId,
            LearningSkillSelectionKind.Learning));
        Assert.Equal(LearningSetupEvidenceStatus.Unavailable,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue())).Status);
    }

    [Fact]
    public void OneEligibleLearningSkill_StillCountsAlongsideIneligibleHistoricalSkills()
    {
        var f = new Fixture();
        var old = Guid.NewGuid();
        f.Preferences.UpdateSelections([], [], [old, f.SkillId]);
        f.Selections.Insert(0, LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), f.Preferences, old,
            LearningSkillSelectionKind.Learning));
        Assert.Equal(LearningSetupEvidenceStatus.Satisfied,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue())).Status);
        Assert.Equal(old, f.Selections[0].SkillId);
    }

    [Fact]
    public void DuplicateValidGoalText_DoesNotInventACompletionRestriction()
    {
        var f = new Fixture();
        var goal = f.Preferences.CreateGoal(f.Goals[0].Text);
        f.Goals.Add(LearningPersonalGoal.CreateFromPreferences(f.Preferences, goal.Id));
        Assert.Equal(LearningSetupEvidenceStatus.Satisfied,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(f.Catalogue())).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancellation_PropagatesBeforeReturningAnyEvidence(bool validInput)
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => LearningSetupEvidenceEvaluator.Evaluate(
            validInput ? f.Input(f.Catalogue()) : null, cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.Empty(error.Data);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemovingFinalRequirement_RecalculatesWithoutChangingProfileOrHistoricalSnapshots(bool goal)
    {
        var f = new Fixture();
        var catalogue = f.Catalogue();
        var before = f.Input(catalogue);
        var account = f.Preferences.AccountId;
        var profile = f.Preferences.LearningProfileId;
        var historicalSelection = f.Selections.Single(row => row.Kind == LearningSkillSelectionKind.Learning);
        var historicalGoal = f.Goals[0];
        Assert.Equal(LearningSetupEvidenceStatus.Satisfied, LearningSetupEvidenceEvaluator.Evaluate(before).Status);
        if (goal)
        {
            f.Preferences.RemoveGoal(historicalGoal.Id);
            f.Goals.Clear();
        }
        else
        {
            f.Preferences.UpdateSelections([], [f.SkillId], []);
            f.Selections.RemoveAll(row => row.Kind == LearningSkillSelectionKind.Learning);
        }
        Assert.Equal(LearningSetupEvidenceStatus.Incomplete,
            LearningSetupEvidenceEvaluator.Evaluate(f.Input(catalogue)).Status);
        Assert.Equal(LearningSetupEvidenceStatus.Satisfied, LearningSetupEvidenceEvaluator.Evaluate(before).Status);
        Assert.Equal(account, f.Preferences.AccountId);
        Assert.Equal(profile, f.Preferences.LearningProfileId);
        Assert.Equal("learning", f.Preferences.Module);
        Assert.Equal(f.SkillId, historicalSelection.SkillId);
        Assert.Equal(profile, historicalGoal.LearningProfileId);
    }

    [Fact]
    public void InputCopiesAndEvaluation_DoNotWriteRetryOrMutateSourceEvidence()
    {
        var f = new Fixture();
        var input = f.Input(f.Catalogue());
        var selections = f.Selections.ToArray();
        var goals = f.Goals.ToArray();
        var interests = f.Preferences.Interests;
        f.Selections.Clear(); f.Goals.Clear();
        var first = LearningSetupEvidenceEvaluator.Evaluate(input);
        var second = LearningSetupEvidenceEvaluator.Evaluate(input);
        Assert.Equal(LearningSetupEvidenceStatus.Satisfied, first.Status);
        Assert.Equal(first.Status, second.Status);
        Assert.Same(interests, f.Preferences.Interests);
        Assert.All(selections, row => Assert.Equal(f.Preferences.LearningProfileId, row.LearningProfileId));
        Assert.All(goals, row => Assert.Equal(f.Preferences.LearningProfileId, row.LearningProfileId));
        Assert.Single(f.Preferences.Goals);
        Assert.Single(f.Preferences.LearningSkills);
        Assert.False(typeof(LearningSetupEvidenceEvaluator).IsPublic);
        // There is no reader, write, retry or asynchronous dependency in this pure evaluator.
    }

    [Fact]
    public void DefaultJsonAndDiagnostics_OmitPrivateEvidence()
    {
        var f = new Fixture();
        var input = f.Input(f.Catalogue());
        var result = LearningSetupEvidenceEvaluator.Evaluate(input);
        var options = new JsonSerializerOptions();
        var inputJson = JsonSerializer.Serialize((object)input, input.GetType(), options);
        var resultJson = JsonSerializer.Serialize((object)result, result.GetType(), options);
        Assert.True(inputJson == "{}" && resultJson == "{}", "Default JSON must omit private simulation evidence.");
        var diagnostics = input.ToString() + result + input.Selections![0] + input.Goals![0];
        foreach (var value in new[] { f.Preferences.AccountId.ToString(), f.Preferences.LearningProfileId.ToString(),
            f.SkillId.ToString(), f.Goals[0].Text, input.CatalogueEvidence!.Revision! })
            Assert.True(!diagnostics.Contains(value, StringComparison.Ordinal), "Diagnostics must omit private evidence.");
        Assert.Equal(LearningSetupEvidenceStatus.Satisfied, result.Status);
    }
}
