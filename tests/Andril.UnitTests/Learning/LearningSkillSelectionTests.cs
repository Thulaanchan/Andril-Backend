using System.Reflection;
using System.Text.Json;
using Andril.Learning.Entities;
using Xunit;

namespace Andril.UnitTests.Learning;

/// <summary>Offline factory snapshots only; no catalogue, authorization or persistence authority.</summary>
public sealed class LearningSkillSelectionTests
{
    [Theory]
    [InlineData(LearningSkillSelectionKind.Teaching)]
    [InlineData(LearningSkillSelectionKind.Learning)]
    public void ValidFactorySnapshot_RetainsIdentityAndScopeAfterPreferencesAreReplaced(
        LearningSkillSelectionKind kind)
    {
        var f = new Fixture();
        var before = new PreferenceSnapshot(f.Preferences);
        var id = Guid.NewGuid();
        var skill = f.SkillFor(kind);
        var row = LearningSkillSelection.CreateFromPreferences(id, f.Preferences, skill, kind);

        AssertRow(row, id, f.Preferences.LearningProfileId, skill, kind);
        before.AssertUnchanged(f.Preferences);
        f.Preferences.UpdateSelections([], [], []);
        f.Preferences.EditGoal(f.Goal.Id, "Changed private goal");

        AssertRow(row, id, f.Preferences.LearningProfileId, skill, kind);
        Assert.Empty(f.Preferences.TeachingSkills);
        Assert.Empty(f.Preferences.LearningSkills);
    }

    [Fact]
    public void SharedSkillSnapshots_KeepBothPurposesAndSeparateLearningProfilesIndependent()
    {
        var first = new Fixture();
        var second = new Fixture();
        second.Preferences.UpdateSelections([], [first.Shared], [first.Shared]);
        var secondBefore = new PreferenceSnapshot(second.Preferences);
        var teaching = LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), first.Preferences,
            first.Shared, LearningSkillSelectionKind.Teaching);
        var learning = LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), first.Preferences,
            first.Shared, LearningSkillSelectionKind.Learning);
        var other = LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), second.Preferences,
            first.Shared, LearningSkillSelectionKind.Learning);

        first.Preferences.UpdateSelections([], [], []);

        Assert.Equal(first.Shared, teaching.SkillId);
        Assert.Equal(first.Shared, learning.SkillId);
        Assert.Equal(LearningSkillSelectionKind.Teaching, teaching.Kind);
        Assert.Equal(LearningSkillSelectionKind.Learning, learning.Kind);
        Assert.Equal(first.Preferences.LearningProfileId, teaching.LearningProfileId);
        Assert.Equal(first.Preferences.LearningProfileId, learning.LearningProfileId);
        Assert.Equal(second.Preferences.LearningProfileId, other.LearningProfileId);
        Assert.NotEqual(learning.LearningProfileId, other.LearningProfileId);
        Assert.NotEqual(teaching.Id, learning.Id);
        Assert.NotSame(learning, other);
        secondBefore.AssertUnchanged(second.Preferences);
    }

    [Fact]
    public void NullPreferences_AreRejectedWithTheActualParameterContract()
    {
        Assert.Throws<ArgumentNullException>("preferences", () =>
            LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), null!, Guid.NewGuid(),
                LearningSkillSelectionKind.Learning));
    }

    [Theory]
    [InlineData(LearningSkillSelectionKind.Teaching, "empty-id", "id")]
    [InlineData(LearningSkillSelectionKind.Learning, "empty-id", "id")]
    [InlineData(LearningSkillSelectionKind.Teaching, "empty-skill", "skillId")]
    [InlineData(LearningSkillSelectionKind.Learning, "empty-skill", "skillId")]
    [InlineData(LearningSkillSelectionKind.Teaching, "missing-skill", "skillId")]
    [InlineData(LearningSkillSelectionKind.Learning, "missing-skill", "skillId")]
    [InlineData(LearningSkillSelectionKind.Teaching, "wrong-list", "skillId")]
    [InlineData(LearningSkillSelectionKind.Learning, "wrong-list", "skillId")]
    public void RejectedFactoryCall_PreservesAllPreferencesAndAnExistingSnapshot(
        LearningSkillSelectionKind kind, string defect, string parameter)
    {
        var f = new Fixture();
        var existingId = Guid.NewGuid();
        var existing = LearningSkillSelection.CreateFromPreferences(existingId, f.Preferences, f.Shared, kind);
        var before = new PreferenceSnapshot(f.Preferences);
        var id = defect == "empty-id" ? Guid.Empty : Guid.NewGuid();
        var skill = defect switch
        {
            "empty-skill" => Guid.Empty,
            "missing-skill" => Guid.NewGuid(),
            "wrong-list" => kind == LearningSkillSelectionKind.Teaching ? f.LearningOnly : f.TeachingOnly,
            _ => f.SkillFor(kind)
        };

        var error = Assert.Throws<ArgumentException>(parameter, () =>
            LearningSkillSelection.CreateFromPreferences(id, f.Preferences, skill, kind));

        before.AssertUnchanged(f.Preferences);
        AssertRow(existing, existingId, f.Preferences.LearningProfileId, f.Shared, kind);
        AssertSafeDiagnostic(error.ToString(), f, id, skill, existingId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void UndefinedKind_IsRejectedWithoutChangingPreferencesOrExistingRows(int value)
    {
        var f = new Fixture();
        var id = Guid.NewGuid();
        var existing = LearningSkillSelection.CreateFromPreferences(id, f.Preferences, f.Shared,
            LearningSkillSelectionKind.Learning);
        var before = new PreferenceSnapshot(f.Preferences);
        var candidateId = Guid.NewGuid();

        var error = Assert.Throws<ArgumentException>("kind", () =>
            LearningSkillSelection.CreateFromPreferences(candidateId, f.Preferences, f.Shared,
                (LearningSkillSelectionKind)value));

        before.AssertUnchanged(f.Preferences);
        AssertRow(existing, id, f.Preferences.LearningProfileId, f.Shared, LearningSkillSelectionKind.Learning);
        AssertSafeDiagnostic(error.ToString(), f, candidateId, id);
    }

    [Theory]
    [InlineData(LearningSkillSelectionKind.Teaching)]
    [InlineData(LearningSkillSelectionKind.Learning)]
    public void CopyingSelectedSkill_DoesNotConsumeSelectionsOrChangeGoalsOrAnotherProfile(
        LearningSkillSelectionKind kind)
    {
        var f = new Fixture();
        var other = new Fixture();
        var before = new PreferenceSnapshot(f.Preferences);
        var otherBefore = new PreferenceSnapshot(other.Preferences);
        var firstId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        var first = LearningSkillSelection.CreateFromPreferences(firstId, f.Preferences, f.SkillFor(kind), kind);
        var next = LearningSkillSelection.CreateFromPreferences(nextId, f.Preferences, f.SkillFor(kind), kind);

        Assert.NotSame(first, next);
        AssertRow(first, firstId, f.Preferences.LearningProfileId, f.SkillFor(kind), kind);
        AssertRow(next, nextId, f.Preferences.LearningProfileId, f.SkillFor(kind), kind);
        before.AssertUnchanged(f.Preferences);
        otherBefore.AssertUnchanged(other.Preferences);
    }

    [Theory]
    [InlineData(LearningSkillSelectionKind.Teaching)]
    [InlineData(LearningSkillSelectionKind.Learning)]
    public void DefaultJsonAndDiagnostics_OmitEveryPrivateFieldWithoutChangingTheSnapshot(
        LearningSkillSelectionKind kind)
    {
        var f = new Fixture();
        var before = new PreferenceSnapshot(f.Preferences);
        var id = Guid.NewGuid();
        var row = LearningSkillSelection.CreateFromPreferences(id, f.Preferences, f.SkillFor(kind), kind);

        Assert.Equal("{}", JsonSerializer.Serialize(row));
        var diagnostic = row.ToString();
        Assert.Equal("Private Learning skill selection [identity, profile and skill omitted]", diagnostic);
        AssertSafeDiagnostic(diagnostic, f, id);
        AssertRow(row, id, f.Preferences.LearningProfileId, f.SkillFor(kind), kind);
        before.AssertUnchanged(f.Preferences);
    }

    [Fact]
    public void EntitySurface_HasNoPublicConstructorOwnershipSetterOrMutableField()
    {
        var type = typeof(LearningSkillSelection);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors());
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.Null(property.GetSetMethod());
        var factory = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal(nameof(LearningSkillSelection.CreateFromPreferences), factory.Name);
        Assert.Equal(new[] { typeof(Guid), typeof(LearningPreferences), typeof(Guid), typeof(LearningSkillSelectionKind) },
            factory.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    private static void AssertRow(LearningSkillSelection row, Guid id, Guid profile, Guid skill,
        LearningSkillSelectionKind kind)
    {
        Assert.Equal(id, row.Id);
        Assert.Equal(profile, row.LearningProfileId);
        Assert.Equal("learning", row.Module);
        Assert.Equal(skill, row.SkillId);
        Assert.Equal(kind, row.Kind);
    }

    private static void AssertSafeDiagnostic(string diagnostic, Fixture f, params Guid[] additionalIds)
    {
        var ids = additionalIds.Concat(new[] { f.Preferences.AccountId, f.Preferences.LearningProfileId,
            f.Interest, f.TeachingOnly, f.LearningOnly, f.Shared, f.Goal.Id });
        foreach (var id in ids.Where(id => id != Guid.Empty))
        {
            Assert.DoesNotContain(id.ToString("D"), diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(id.ToString("N"), diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(f.Goal.Text, diagnostic, StringComparison.Ordinal);
    }

    private sealed class Fixture
    {
        internal LearningPreferences Preferences { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        internal Guid Interest { get; } = Guid.NewGuid();
        internal Guid TeachingOnly { get; } = Guid.NewGuid();
        internal Guid LearningOnly { get; } = Guid.NewGuid();
        internal Guid Shared { get; } = Guid.NewGuid();
        internal LearningPreferences.PersonalGoal Goal { get; }

        internal Fixture()
        {
            Preferences.UpdateSelections([Interest], [TeachingOnly, Shared], [LearningOnly, Shared]);
            Goal = Preferences.CreateGoal("Synthetic private goal தமிழ் සිංහල e\u0301");
        }

        internal Guid SkillFor(LearningSkillSelectionKind kind) =>
            kind == LearningSkillSelectionKind.Teaching ? TeachingOnly : LearningOnly;
    }

    private sealed class PreferenceSnapshot(LearningPreferences preferences)
    {
        private readonly Guid account = preferences.AccountId;
        private readonly Guid profile = preferences.LearningProfileId;
        private readonly IReadOnlyList<Guid> interests = preferences.Interests;
        private readonly IReadOnlyList<Guid> teaching = preferences.TeachingSkills;
        private readonly IReadOnlyList<Guid> learning = preferences.LearningSkills;
        private readonly IReadOnlyList<LearningPreferences.PersonalGoal> goals = preferences.Goals;
        private readonly Guid[] interestValues = preferences.Interests.ToArray();
        private readonly Guid[] teachingValues = preferences.TeachingSkills.ToArray();
        private readonly Guid[] learningValues = preferences.LearningSkills.ToArray();
        private readonly LearningPreferences.PersonalGoal[] goalValues = preferences.Goals.ToArray();

        internal void AssertUnchanged(LearningPreferences current)
        {
            Assert.Equal(account, current.AccountId);
            Assert.Equal(profile, current.LearningProfileId);
            Assert.Equal("learning", current.Module);
            Assert.Same(interests, current.Interests);
            Assert.Same(teaching, current.TeachingSkills);
            Assert.Same(learning, current.LearningSkills);
            Assert.Same(goals, current.Goals);
            Assert.Equal(interestValues, current.Interests.ToArray());
            Assert.Equal(teachingValues, current.TeachingSkills.ToArray());
            Assert.Equal(learningValues, current.LearningSkills.ToArray());
            Assert.Equal(goalValues, current.Goals.ToArray());
        }
    }
}
