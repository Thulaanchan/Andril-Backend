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
        preferences.UpdateSelections([Guid.NewGuid()], [Guid.NewGuid()], [Guid.NewGuid()]);
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
    public void SkillOverlap_IsExplicitlyUnsupportedPendingApproval_AndPreservesState()
    {
        var preferences = Create();
        preferences.UpdateSelections([Guid.NewGuid()], [Guid.NewGuid()], [Guid.NewGuid()]);
        var prior = preferences.TeachingSkills;
        var id = Guid.NewGuid();
        Assert.Throws<NotSupportedException>(() => preferences.UpdateSelections([], [id], [id]));
        Assert.Same(prior, preferences.TeachingSkills);
        Assert.NotEmpty(preferences.Interests);
        Assert.NotEmpty(preferences.LearningSkills);
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
}
