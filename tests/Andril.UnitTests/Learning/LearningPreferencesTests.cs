using Andril.Learning.Entities;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningPreferencesTests
{
    private static LearningPreferences Create() => new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Construction_BindsLearningOnlyWithoutGrantingPermissions()
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var preferences = new LearningPreferences(account, profile);
        Assert.Equal(account, preferences.AccountId);
        Assert.Equal(profile, preferences.LearningProfileId);
        Assert.Equal("learning", preferences.Module);
        Assert.Empty(preferences.Interests);
        Assert.Empty(preferences.TeachingSkills);
        Assert.Empty(preferences.LearningSkills);
        Assert.Empty(preferences.Goals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyOwnershipIdentity_IsRejected(bool emptyAccount)
    {
        Assert.Throws<ArgumentException>(() => new LearningPreferences(
            emptyAccount ? Guid.Empty : Guid.NewGuid(), emptyAccount ? Guid.NewGuid() : Guid.Empty));
    }

    [Fact]
    public void Update_SupportsMultipleSelectionsInAllThreeCollections()
    {
        var preferences = Create();
        var interests = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var teaching = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var learning = new[] { Guid.NewGuid(), Guid.NewGuid() };
        preferences.UpdateSelections(interests, teaching, learning);
        Assert.Equal(interests, preferences.Interests);
        Assert.Equal(teaching, preferences.TeachingSkills);
        Assert.Equal(learning, preferences.LearningSkills);
    }

    [Fact]
    public void Update_ReplacesAndClearsSelectionsWithoutChangingGoalsOrScope()
    {
        var preferences = Create();
        var goal = preferences.CreateGoal("Learn a subject");
        var account = preferences.AccountId;
        var profile = preferences.LearningProfileId;
        preferences.UpdateSelections([Guid.NewGuid()], [Guid.NewGuid()], [Guid.NewGuid()]);
        var next = Guid.NewGuid();
        preferences.UpdateSelections([next], [], []);
        Assert.Equal(new[] { next }, preferences.Interests);
        Assert.Empty(preferences.TeachingSkills);
        Assert.Empty(preferences.LearningSkills);
        Assert.Same(goal, Assert.Single(preferences.Goals));
        Assert.Equal(account, preferences.AccountId);
        Assert.Equal(profile, preferences.LearningProfileId);
    }

    [Theory]
    [InlineData(0, "duplicate")]
    [InlineData(1, "duplicate")]
    [InlineData(2, "duplicate")]
    [InlineData(0, "empty")]
    [InlineData(1, "empty")]
    [InlineData(2, "empty")]
    [InlineData(0, "null")]
    [InlineData(1, "null")]
    [InlineData(2, "null")]
    [InlineData(0, "enumeration")]
    [InlineData(1, "enumeration")]
    [InlineData(2, "enumeration")]
    public void RejectedUpdate_PreservesEveryCollectionAndGoal(int collection, string defect)
    {
        var preferences = Create();
        var shared = Guid.NewGuid();
        preferences.UpdateSelections([Guid.NewGuid()], [shared], [shared]);
        preferences.CreateGoal("Keep private");
        var interests = preferences.Interests;
        var teaching = preferences.TeachingSkills;
        var learning = preferences.LearningSkills;
        var goals = preferences.Goals;
        var id = Guid.NewGuid();
        IEnumerable<Guid>? bad = defect switch
        {
            "duplicate" => new[] { id, id }, "empty" => new[] { Guid.Empty },
            "null" => null, _ => FailingEnumeration()
        };
        var next = new IEnumerable<Guid>[] { new[] { Guid.NewGuid() }, new[] { Guid.NewGuid() }, new[] { Guid.NewGuid() } };
        next[collection] = bad!;
        Assert.ThrowsAny<Exception>(() => preferences.UpdateSelections(next[0], next[1], next[2]));
        Assert.Same(interests, preferences.Interests);
        Assert.Same(teaching, preferences.TeachingSkills);
        Assert.Same(learning, preferences.LearningSkills);
        Assert.Same(goals, preferences.Goals);
    }

    private static IEnumerable<Guid> FailingEnumeration()
    {
        yield return Guid.NewGuid();
        throw new InvalidOperationException("Synthetic enumeration failure.");
    }

    [Fact]
    public void SameSkillInTeachingAndLearning_SucceedsWithIndependentSnapshotsAndUnchangedScope()
    {
        var preferences = Create();
        var account = preferences.AccountId; var profile = preferences.LearningProfileId;
        var shared = Guid.NewGuid(); var teachingOnly = Guid.NewGuid(); var learningOnly = Guid.NewGuid();
        var teaching = new[] { shared, teachingOnly }; var learning = new[] { shared, learningOnly };
        var goal = preferences.CreateGoal("Keep private");
        preferences.UpdateSelections([Guid.NewGuid()], teaching, learning);
        teaching[0] = Guid.NewGuid(); learning[0] = Guid.NewGuid();
        Assert.Equal(new[] { shared, teachingOnly }, preferences.TeachingSkills);
        Assert.Equal(new[] { shared, learningOnly }, preferences.LearningSkills);
        Assert.NotSame(preferences.TeachingSkills, preferences.LearningSkills);
        Assert.Equal(account, preferences.AccountId); Assert.Equal(profile, preferences.LearningProfileId);
        Assert.Equal("learning", preferences.Module);
        Assert.Same(goal, Assert.Single(preferences.Goals));
        Assert.Single(preferences.Interests);
        var teachingRow = LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), preferences, shared,
            LearningSkillSelectionKind.Teaching);
        var learningRow = LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), preferences, shared,
            LearningSkillSelectionKind.Learning);
        Assert.Equal(shared, teachingRow.SkillId); Assert.Equal(shared, learningRow.SkillId);
        Assert.Equal(LearningSkillSelectionKind.Teaching, teachingRow.Kind);
        Assert.Equal(LearningSkillSelectionKind.Learning, learningRow.Kind);
        Assert.Equal(profile, teachingRow.LearningProfileId); Assert.Equal(profile, learningRow.LearningProfileId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateWithinEitherSkillList_IsRejectedAndPreservesValidOverlappingState(bool duplicateTeaching)
    {
        var preferences = Create(); var shared = Guid.NewGuid();
        preferences.UpdateSelections([Guid.NewGuid()], [shared], [shared]);
        var goal = preferences.CreateGoal("Keep private");
        var interests = preferences.Interests; var teaching = preferences.TeachingSkills;
        var learning = preferences.LearningSkills; var goals = preferences.Goals;
        Assert.Throws<ArgumentException>(duplicateTeaching ? "teachingSkills" : "learningSkills", () =>
            preferences.UpdateSelections([Guid.NewGuid()], duplicateTeaching ? [shared, shared] : [shared],
                duplicateTeaching ? [shared] : [shared, shared]));
        Assert.Same(interests, preferences.Interests); Assert.Same(teaching, preferences.TeachingSkills);
        Assert.Same(learning, preferences.LearningSkills); Assert.Same(goals, preferences.Goals);
        Assert.Equal(shared, Assert.Single(preferences.TeachingSkills));
        Assert.Equal(shared, Assert.Single(preferences.LearningSkills));
        Assert.Same(goal, Assert.Single(preferences.Goals));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClearingOneOverlappingSkillList_DoesNotClearTheOtherOrAnotherProfile(bool clearTeaching)
    {
        var preferences = Create(); var shared = Guid.NewGuid();
        var other = Create(); other.UpdateSelections([], [Guid.NewGuid()], [Guid.NewGuid()]);
        var otherTeaching = other.TeachingSkills; var otherLearning = other.LearningSkills;
        preferences.UpdateSelections([], [shared], [shared]);
        var priorTeaching = preferences.TeachingSkills; var priorLearning = preferences.LearningSkills;
        preferences.UpdateSelections([], clearTeaching ? [] : [shared], clearTeaching ? [shared] : []);
        Assert.Empty(clearTeaching ? preferences.TeachingSkills : preferences.LearningSkills);
        Assert.Equal(shared, Assert.Single(clearTeaching ? preferences.LearningSkills : preferences.TeachingSkills));
        Assert.Equal(shared, Assert.Single(priorTeaching)); Assert.Equal(shared, Assert.Single(priorLearning));
        Assert.Same(otherTeaching, other.TeachingSkills); Assert.Same(otherLearning, other.LearningSkills);
    }

    [Fact]
    public void ReapplyingSelections_DoesNotIntroduceDuplicates()
    {
        var preferences = Create();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        preferences.UpdateSelections(ids, [], []);
        preferences.UpdateSelections(ids, [], []);
        Assert.Equal(ids, preferences.Interests);
    }

    [Fact]
    public void Selections_AreCopiedAndReturnedAsUnmodifiableSnapshots()
    {
        var preferences = Create();
        var inputs = new[] { new[] { Guid.NewGuid() }, new[] { Guid.NewGuid() }, new[] { Guid.NewGuid() } };
        var expected = inputs.Select(x => x[0]).ToArray();
        preferences.UpdateSelections(inputs[0], inputs[1], inputs[2]);
        var snapshots = new[] { preferences.Interests, preferences.TeachingSkills, preferences.LearningSkills };
        for (var i = 0; i < 3; i++)
        {
            inputs[i][0] = Guid.NewGuid();
            Assert.Equal(expected[i], Assert.Single(snapshots[i]));
            Assert.Throws<NotSupportedException>(() => ((IList<Guid>)snapshots[i]).Add(Guid.NewGuid()));
            Assert.Throws<NotSupportedException>(() => ((IList<Guid>)snapshots[i])[0] = Guid.NewGuid());
        }
        preferences.UpdateSelections([], [], []);
        Assert.All(snapshots, x => Assert.Single(x));
    }

    [Fact]
    public void CreateGoals_UsesDistinctIdsAndPrivateExactTextSnapshots()
    {
        var preferences = Create();
        const string text = "  Learn தமிழ் and සිංහල 👩🏽‍💻 e\u0301  ";
        var first = preferences.CreateGoal(text);
        var second = preferences.CreateGoal(text);
        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(text, first.Text);
        Assert.Equal(2, preferences.Goals.Count);
        Assert.All(preferences.Goals, goal => Assert.True(goal.IsPrivate));
        // No business rule prohibits identical goal text; duplicate selection rules apply to selections.
    }

    [Fact]
    public void EditGoal_PreservesIdentityPrivacyAndOtherGoals()
    {
        var preferences = Create();
        var first = preferences.CreateGoal("Original");
        var second = preferences.CreateGoal("Other");
        var snapshot = preferences.Goals;
        var updated = preferences.EditGoal(first.Id, "  Updated  ");
        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("  Updated  ", updated.Text);
        Assert.True(updated.IsPrivate);
        Assert.Equal("Original", first.Text);
        Assert.Equal("Original", snapshot[0].Text);
        Assert.Same(second, preferences.Goals[1]);
    }

    [Fact]
    public void RemoveGoal_RemovesOnlyTheSelectedLocalGoal()
    {
        var preferences = Create();
        var first = preferences.CreateGoal("First");
        var second = preferences.CreateGoal("Second");
        var snapshot = preferences.Goals;
        preferences.RemoveGoal(first.Id);
        Assert.Same(second, Assert.Single(preferences.Goals));
        Assert.Equal(2, snapshot.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void InvalidGoalText_CannotCreateOrEditAndPreservesState(string? text)
    {
        var preferences = Create();
        var goal = preferences.CreateGoal("Keep");
        var snapshot = preferences.Goals;
        Assert.Throws<ArgumentException>(() => preferences.CreateGoal(text!));
        Assert.Throws<ArgumentException>(() => preferences.EditGoal(goal.Id, text!));
        Assert.Same(snapshot, preferences.Goals);
        Assert.Equal("Keep", goal.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownGoal_CannotMutateAnotherInstance(bool emptyId)
    {
        var preferences = Create();
        preferences.CreateGoal("Local");
        var foreign = Create().CreateGoal("Foreign");
        var id = emptyId ? Guid.Empty : foreign.Id;
        var snapshot = preferences.Goals;
        if (emptyId)
        {
            Assert.Throws<ArgumentException>(() => preferences.EditGoal(id, "Changed"));
            Assert.Throws<ArgumentException>(() => preferences.RemoveGoal(id));
        }
        else
        {
            Assert.Throws<KeyNotFoundException>(() => preferences.EditGoal(id, "Changed"));
            Assert.Throws<KeyNotFoundException>(() => preferences.RemoveGoal(id));
        }
        Assert.Same(snapshot, preferences.Goals);
        Assert.Equal("Foreign", foreign.Text);
    }

    [Fact]
    public void Goals_CannotBeInsertedReplacedOrRemovedThroughReturnedCollection()
    {
        var preferences = Create();
        var goal = preferences.CreateGoal("Private");
        var list = (IList<LearningPreferences.PersonalGoal>)preferences.Goals;
        Assert.Throws<NotSupportedException>(() => list.Add(goal));
        Assert.Throws<NotSupportedException>(() => list[0] = goal);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Single(preferences.Goals);
    }

    [Fact]
    public void DomainInstances_DoNotShareStateEvenWhenGivenTheSameUnverifiedScope()
    {
        var preferences = Create();
        var other = new LearningPreferences(preferences.AccountId, preferences.LearningProfileId);
        preferences.CreateGoal("Private");
        preferences.UpdateSelections([Guid.NewGuid()], [], []);
        Assert.Empty(other.Goals);
        Assert.Empty(other.Interests);
        // This is memory isolation only, not proof of persisted ownership or module authorization.
    }

    [Fact]
    public void Diagnostics_OmitPrivateGoalTextAndScope()
    {
        var preferences = Create();
        var goal = preferences.CreateGoal("PRIVATE-GOAL-MARKER");
        var text = preferences.ToString() + goal;
        Assert.DoesNotContain(goal.Text, text);
        Assert.DoesNotContain(preferences.AccountId.ToString(), text);
        Assert.DoesNotContain(preferences.LearningProfileId.ToString(), text);
        Assert.DoesNotContain(goal.Id.ToString(), text);
    }

    [Fact]
    public void CatalogueUpdate_AcceptsOverlapAndCopiesSelectionsWithoutChangingGoalsOrScope()
    {
        var preferences = Create();
        var account = preferences.AccountId; var profile = preferences.LearningProfileId;
        var goal = preferences.CreateGoal("Keep private");
        var goals = preferences.Goals;
        var shared = Guid.NewGuid(); var teachingOnly = Guid.NewGuid(); var learningOnly = Guid.NewGuid();
        var interests = new[] { Guid.NewGuid() }; // Interests do not require skill-catalogue membership.
        var interest = interests[0];
        var teaching = new[] { shared, teachingOnly }; var learning = new[] { shared, learningOnly };
        var catalogue = new LearningSkillCatalogue(new[] { shared, teachingOnly, learningOnly }
            .Select(id => new LearningSkillCatalogue.Skill(id, true, LearningSkillCatalogueStatus.Active)));

        preferences.UpdateSelections(interests, teaching, learning, catalogue);
        interests[0] = Guid.NewGuid(); teaching[0] = Guid.NewGuid(); learning[0] = Guid.NewGuid();

        Assert.Equal(new[] { interest }, preferences.Interests);
        Assert.Equal(new[] { shared, teachingOnly }, preferences.TeachingSkills);
        Assert.Equal(new[] { shared, learningOnly }, preferences.LearningSkills);
        Assert.NotSame(preferences.TeachingSkills, preferences.LearningSkills);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)preferences.TeachingSkills).Clear());
        Assert.Same(goals, preferences.Goals); Assert.Same(goal, Assert.Single(preferences.Goals));
        Assert.Equal(account, preferences.AccountId); Assert.Equal(profile, preferences.LearningProfileId);
        Assert.Equal("learning", preferences.Module);
        Assert.Contains("not authorization evidence", catalogue.ToString());
        // Local validation establishes no production access or authoritative catalogue provenance.
    }

    [Theory]
    [InlineData(true, "missing")]
    [InlineData(false, "missing")]
    [InlineData(true, "unapproved")]
    [InlineData(false, "unapproved")]
    [InlineData(true, "retired")]
    [InlineData(false, "retired")]
    [InlineData(true, "superseded")]
    [InlineData(false, "superseded")]
    [InlineData(true, "merged")]
    [InlineData(false, "merged")]
    public void CatalogueUpdate_RejectedSkillPreservesAllState(bool inTeaching, string defect)
    {
        var preferences = Create(); var old = Guid.NewGuid();
        preferences.UpdateSelections([Guid.NewGuid()], [old], [old]);
        preferences.CreateGoal("Keep private");
        var interests = preferences.Interests; var teaching = preferences.TeachingSkills;
        var learning = preferences.LearningSkills; var goals = preferences.Goals;
        var account = preferences.AccountId; var profile = preferences.LearningProfileId;
        var valid = Guid.NewGuid(); var invalid = Guid.NewGuid();
        var status = defect switch
        {
            "retired" => LearningSkillCatalogueStatus.Retired,
            "superseded" => LearningSkillCatalogueStatus.Superseded,
            "merged" => LearningSkillCatalogueStatus.Merged,
            _ => LearningSkillCatalogueStatus.Active
        };
        var skills = new List<LearningSkillCatalogue.Skill>
        {
            new(valid, true, LearningSkillCatalogueStatus.Active)
        };
        if (defect != "missing") skills.Add(new(invalid, defect != "unapproved", status));
        var catalogue = new LearningSkillCatalogue(skills);

        var error = Assert.Throws<ArgumentException>(inTeaching ? "teachingSkills" : "learningSkills", () =>
            preferences.UpdateSelections([Guid.NewGuid()], inTeaching ? [valid, invalid] : [valid],
                inTeaching ? [valid] : [valid, invalid], catalogue));

        Assert.Same(interests, preferences.Interests); Assert.Same(teaching, preferences.TeachingSkills);
        Assert.Same(learning, preferences.LearningSkills); Assert.Same(goals, preferences.Goals);
        Assert.Equal(account, preferences.AccountId); Assert.Equal(profile, preferences.LearningProfileId);
        Assert.DoesNotContain(invalid.ToString(), error.ToString());
    }

    [Theory]
    [InlineData(0, "duplicate")]
    [InlineData(1, "duplicate")]
    [InlineData(2, "duplicate")]
    [InlineData(0, "empty")]
    [InlineData(1, "empty")]
    [InlineData(2, "empty")]
    [InlineData(0, "null")]
    [InlineData(1, "null")]
    [InlineData(2, "null")]
    [InlineData(0, "enumeration")]
    [InlineData(1, "enumeration")]
    [InlineData(2, "enumeration")]
    public void CatalogueUpdate_InvalidInputPreservesAllState(int collection, string defect)
    {
        var preferences = Create(); var id = Guid.NewGuid();
        preferences.UpdateSelections([id], [id], [id]);
        preferences.CreateGoal("Keep private");
        var interests = preferences.Interests; var teaching = preferences.TeachingSkills;
        var learning = preferences.LearningSkills; var goals = preferences.Goals;
        var catalogue = new LearningSkillCatalogue([new(id, true, LearningSkillCatalogueStatus.Active)]);
        IEnumerable<Guid>? bad = defect switch
        {
            "duplicate" => new[] { id, id }, "empty" => new[] { Guid.Empty },
            "null" => null, _ => FailingEnumeration()
        };
        var next = new IEnumerable<Guid>[] { new[] { id }, new[] { id }, new[] { id } };
        next[collection] = bad!;

        Assert.ThrowsAny<Exception>(() => preferences.UpdateSelections(next[0], next[1], next[2], catalogue));
        Assert.Same(interests, preferences.Interests); Assert.Same(teaching, preferences.TeachingSkills);
        Assert.Same(learning, preferences.LearningSkills); Assert.Same(goals, preferences.Goals);
    }

    [Fact]
    public void CatalogueUpdate_NullCataloguePreservesAllState()
    {
        var preferences = Create(); var id = Guid.NewGuid();
        preferences.UpdateSelections([id], [id], [id]); preferences.CreateGoal("Keep private");
        var interests = preferences.Interests; var teaching = preferences.TeachingSkills;
        var learning = preferences.LearningSkills; var goals = preferences.Goals;
        Assert.Throws<ArgumentNullException>("catalogue", () => preferences.UpdateSelections([], [], [], null!));
        Assert.Same(interests, preferences.Interests); Assert.Same(teaching, preferences.TeachingSkills);
        Assert.Same(learning, preferences.LearningSkills); Assert.Same(goals, preferences.Goals);
    }

    [Fact]
    public void CatalogueUpdate_EnumeratesCallerInputsOnceAndKeepsTheValidatedValues()
    {
        var preferences = Create(); var id = Guid.NewGuid();
        var calls = new int[3];
        IEnumerable<Guid> Once(int index)
        {
            if (++calls[index] != 1) throw new InvalidOperationException("Input enumerated again.");
            yield return id;
        }
        var catalogue = new LearningSkillCatalogue([new(id, true, LearningSkillCatalogueStatus.Active)]);
        preferences.UpdateSelections(Once(0), Once(1), Once(2), catalogue);
        Assert.Equal(new[] { 1, 1, 1 }, calls);
        Assert.Equal(id, Assert.Single(preferences.Interests));
        Assert.Equal(id, Assert.Single(preferences.TeachingSkills));
        Assert.Equal(id, Assert.Single(preferences.LearningSkills));
    }

    [Fact]
    public void CatalogueUpdate_CanExplicitlyClearSelectionsWithoutTouchingHistoricalSnapshotsOrAnotherInstance()
    {
        var preferences = Create(); var id = Guid.NewGuid();
        preferences.UpdateSelections([id], [id], [id]); preferences.CreateGoal("Keep private");
        var teaching = preferences.TeachingSkills; var learning = preferences.LearningSkills;
        var goals = preferences.Goals;
        var other = new LearningPreferences(preferences.AccountId, preferences.LearningProfileId);
        other.UpdateSelections([id], [id], [id]);
        var otherTeaching = other.TeachingSkills;

        preferences.UpdateSelections([], [], [], new LearningSkillCatalogue([]));

        Assert.Empty(preferences.Interests); Assert.Empty(preferences.TeachingSkills); Assert.Empty(preferences.LearningSkills);
        Assert.Same(goals, preferences.Goals);
        Assert.Equal(id, Assert.Single(teaching)); Assert.Equal(id, Assert.Single(learning));
        Assert.Same(otherTeaching, other.TeachingSkills);
        Assert.Equal(id, Assert.Single(other.TeachingSkills));
    }
}
