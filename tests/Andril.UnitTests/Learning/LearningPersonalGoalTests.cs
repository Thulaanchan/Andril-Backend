using System.Reflection;
using System.Text.Json;
using Andril.Learning.Entities;
using Xunit;

namespace Andril.UnitTests.Learning;

/// <summary>Offline goal-copy contract only; no database, ownership authorization or setup-completion proof.</summary>
public sealed class LearningPersonalGoalTests
{
    [Theory]
    [InlineData("  Learn Tamil\r\nwithout trimming  ")]
    [InlineData("தமிழ் கற்றல்")]
    [InlineData("සිංහල ඉගෙනීම")]
    [InlineData("e\u0301 👩🏽‍💻 தமிழ் සිංහල\n")]
    [InlineData("\tA\r\nB\t")]
    public void ValidFactory_CopiesExactUnicodeIdentityAndLearningScopeWithoutConsumingGoal(string text)
    {
        var f = new Fixture();
        var goal = f.Preferences.CreateGoal(text);
        var before = new PreferenceSnapshot(f.Preferences);

        var first = LearningPersonalGoal.CreateFromPreferences(f.Preferences, goal.Id);
        var second = LearningPersonalGoal.CreateFromPreferences(f.Preferences, goal.Id);

        Assert.NotEqual(Guid.Empty, goal.Id);
        AssertRow(first, goal.Id, f.Preferences.LearningProfileId, text);
        AssertRow(second, goal.Id, f.Preferences.LearningProfileId, text);
        Assert.NotSame(first, second);
        Assert.Same(goal, f.Preferences.Goals[^1]);
        before.AssertUnchanged(f.Preferences);
    }

    [Fact]
    public void NullPreferences_AreRejectedByTheActualFactoryParameterContract()
    {
        Assert.Throws<ArgumentNullException>("preferences", () =>
            LearningPersonalGoal.CreateFromPreferences(null!, Guid.Parse("10000000-0000-0000-0000-000000000099")));
    }

    [Fact]
    public void EmptyGoalIdentity_IsRejectedWithoutChangingAnyPreferencesOrExistingEntity()
    {
        var f = new Fixture();
        var existing = LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id);
        var before = new PreferenceSnapshot(f.Preferences);

        var error = Assert.Throws<ArgumentException>("goalId", () =>
            LearningPersonalGoal.CreateFromPreferences(f.Preferences, Guid.Empty));

        before.AssertUnchanged(f.Preferences);
        AssertRow(existing, f.Goal.Id, f.Preferences.LearningProfileId, f.Goal.Text);
        AssertSafeDiagnostic(error.ToString(), f.Preferences);
    }

    [Fact]
    public void UnknownGoal_IsRejectedWithoutFallbackOrPreferenceMutation()
    {
        var f = new Fixture();
        var missing = Guid.Parse("10000000-0000-0000-0000-000000000099");
        var existing = LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id);
        var before = new PreferenceSnapshot(f.Preferences);

        var error = Assert.Throws<KeyNotFoundException>(() =>
            LearningPersonalGoal.CreateFromPreferences(f.Preferences, missing));

        before.AssertUnchanged(f.Preferences);
        AssertRow(existing, f.Goal.Id, f.Preferences.LearningProfileId, f.Goal.Text);
        AssertSafeDiagnostic(error.ToString(), f.Preferences, missing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignGoal_IsNotFoundEvenWithEqualTextOrTheSameAccount(bool sameAccount)
    {
        var local = new Fixture();
        var foreign = new Fixture(true, sameAccount);
        Assert.Equal(local.Goal.Text, foreign.Goal.Text);
        var localBefore = new PreferenceSnapshot(local.Preferences);
        var foreignBefore = new PreferenceSnapshot(foreign.Preferences);
        var original = LearningPersonalGoal.CreateFromPreferences(foreign.Preferences, foreign.Goal.Id);

        var error = Assert.Throws<KeyNotFoundException>(() =>
            LearningPersonalGoal.CreateFromPreferences(local.Preferences, foreign.Goal.Id));

        localBefore.AssertUnchanged(local.Preferences);
        foreignBefore.AssertUnchanged(foreign.Preferences);
        AssertRow(original, foreign.Goal.Id, foreign.Preferences.LearningProfileId, foreign.Goal.Text);
        AssertSafeDiagnostic(error.ToString(), local.Preferences, foreign.Goal.Id);
        AssertSafeDiagnostic(error.ToString(), foreign.Preferences);
    }

    [Fact]
    public void RemovedFinalGoal_CannotBeCopiedAndDoesNotRewriteAnEarlierEntitySnapshot()
    {
        var f = new Fixture();
        var row = LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id);
        f.Preferences.RemoveGoal(f.Goal.Id);
        var afterRemoval = new PreferenceSnapshot(f.Preferences);

        var error = Assert.Throws<KeyNotFoundException>(() =>
            LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id));

        Assert.Empty(f.Preferences.Goals);
        afterRemoval.AssertUnchanged(f.Preferences);
        AssertRow(row, f.Goal.Id, f.Preferences.LearningProfileId, f.Goal.Text);
        AssertSafeDiagnostic(error.ToString(), f.Preferences, f.Goal.Id);
        Assert.DoesNotContain(f.Goal.Text, error.ToString(), StringComparison.Ordinal);
        // This proves local removal only, not database deletion, profile activation or setup recalculation.
    }

    [Fact]
    public void DuplicateText_ProducesDistinctGoalEntitiesWithoutChangingEitherIdentity()
    {
        var f = new Fixture();
        var duplicate = f.Preferences.CreateGoal(f.Goal.Text);
        var before = new PreferenceSnapshot(f.Preferences);

        var first = LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id);
        var second = LearningPersonalGoal.CreateFromPreferences(f.Preferences, duplicate.Id);

        Assert.NotEqual(first.Id, second.Id);
        AssertRow(first, f.Goal.Id, f.Preferences.LearningProfileId, f.Goal.Text);
        AssertRow(second, duplicate.Id, f.Preferences.LearningProfileId, duplicate.Text);
        before.AssertUnchanged(f.Preferences);
    }

    [Fact]
    public void EditingOriginalGoal_ReturnsNewContentWithStableIdentityAndLeavesEarlierEntityUnchanged()
    {
        var f = new Fixture();
        var original = LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id);
        var priorGoals = f.Preferences.Goals;
        const string replacement = "  Updated தமிழ் සිංහල e\u0301 👩🏽‍💻\r\n  ";
        var edited = f.Preferences.EditGoal(f.Goal.Id, replacement);
        var afterEdit = new PreferenceSnapshot(f.Preferences);

        var current = LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id);

        AssertRow(original, f.Goal.Id, f.Preferences.LearningProfileId, f.Goal.Text);
        AssertRow(current, f.Goal.Id, f.Preferences.LearningProfileId, replacement);
        Assert.NotSame(original, current);
        Assert.Equal(edited.Id, current.Id);
        Assert.Same(f.Goal, Assert.Single(priorGoals));
        afterEdit.AssertUnchanged(f.Preferences);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void InvalidPreferenceEdit_CannotContaminateTheNextEntityCopy(string? invalidText)
    {
        var f = new Fixture();
        var original = LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id);
        var before = new PreferenceSnapshot(f.Preferences);

        var error = Assert.Throws<ArgumentException>("text", () =>
            f.Preferences.EditGoal(f.Goal.Id, invalidText!));
        var current = LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id);

        before.AssertUnchanged(f.Preferences);
        AssertRow(original, f.Goal.Id, f.Preferences.LearningProfileId, f.Goal.Text);
        AssertRow(current, f.Goal.Id, f.Preferences.LearningProfileId, f.Goal.Text);
        AssertSafeDiagnostic(error.ToString(), f.Preferences);
        // The factory accepts a goal reference, not raw text; text validation belongs to preferences.
    }

    [Fact]
    public void DefaultJsonAndDiagnostics_OmitPrivateContentAndReferencesWithoutMutation()
    {
        var f = new Fixture();
        var before = new PreferenceSnapshot(f.Preferences);
        var row = LearningPersonalGoal.CreateFromPreferences(f.Preferences, f.Goal.Id);

        var json = JsonSerializer.Serialize(row);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Empty(document.RootElement.EnumerateObject());
        Assert.Equal("{}", json);
        var diagnostic = row.ToString();
        Assert.Equal("Private Learning personal goal [identity, profile and text omitted]", diagnostic);
        AssertSafeDiagnostic(json, f.Preferences);
        AssertSafeDiagnostic(diagnostic, f.Preferences);
        AssertRow(row, f.Goal.Id, f.Preferences.LearningProfileId, f.Goal.Text);
        before.AssertUnchanged(f.Preferences);
    }

    [Fact]
    public void PublicSurface_DoesNotAcceptAnIndependentOwnerOrAllowPublicSnapshotMutation()
    {
        var type = typeof(LearningPersonalGoal);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors());
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.Null(property.GetSetMethod());
        var factory = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal(nameof(LearningPersonalGoal.CreateFromPreferences), factory.Name);
        Assert.Equal(new[] { typeof(LearningPreferences), typeof(Guid) },
            factory.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    private static void AssertRow(LearningPersonalGoal row, Guid id, Guid profile, string text)
    {
        Assert.Equal(id, row.Id);
        Assert.Equal(profile, row.LearningProfileId);
        Assert.Equal("learning", row.Module);
        Assert.Equal(text, row.Text);
    }

    private static void AssertSafeDiagnostic(string diagnostic, LearningPreferences preferences, params Guid[] ids)
    {
        foreach (var id in ids.Concat(new[] { preferences.AccountId, preferences.LearningProfileId })
            .Concat(preferences.Goals.Select(goal => goal.Id)))
        {
            Assert.DoesNotContain(id.ToString("D"), diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(id.ToString("N"), diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var goal in preferences.Goals)
            Assert.DoesNotContain(goal.Text, diagnostic, StringComparison.Ordinal);
    }

    private sealed class Fixture
    {
        internal LearningPreferences Preferences { get; }
        internal LearningPreferences.PersonalGoal Goal { get; }

        internal Fixture(bool foreign = false, bool sameAccount = false)
        {
            var account = Guid.Parse(foreign && !sameAccount
                ? "20000000-0000-0000-0000-000000000001" : "10000000-0000-0000-0000-000000000001");
            var profile = Guid.Parse(foreign
                ? "20000000-0000-0000-0000-000000000002" : "10000000-0000-0000-0000-000000000002");
            Preferences = new LearningPreferences(account, profile);
            Preferences.UpdateSelections([Guid.Parse("10000000-0000-0000-0000-000000000003")],
                [Guid.Parse("10000000-0000-0000-0000-000000000004")],
                [Guid.Parse("10000000-0000-0000-0000-000000000005")]);
            Goal = Preferences.CreateGoal("  Synthetic private தமிழ் සිංහල e\u0301 goal  ");
        }
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
