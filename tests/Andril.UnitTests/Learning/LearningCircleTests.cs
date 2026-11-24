using System.Reflection;
using Andril.Learning.Circles;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningCircleTests
{
    private static LearningCircle Create() => new(Guid.NewGuid());

    [Fact]
    public void Construction_PreservesIdentityAndLearningOnlyScope_WithNoMembershipGrant()
    {
        var id = Guid.NewGuid();
        var circle = new LearningCircle(id);
        Assert.Equal(id, circle.Id);
        Assert.Equal("learning", circle.Module);
        Assert.Empty(circle.Goals);
    }

    [Fact]
    public void EmptyCircleIdentity_IsRejected()
    {
        Assert.Throws<ArgumentException>("id", () => new LearningCircle(Guid.Empty));
    }

    [Theory]
    [InlineData("Learn algebra")]
    [InlineData("தமிழ் கற்போம்")]
    [InlineData("සිංහල ඉගෙන ගනිමු")]
    [InlineData("  Keep whitespace  ")]
    [InlineData("e\u0301 👩🏽‍💻")]
    public void SharedGoalCreation_PreservesExactTextAndCircleBinding(string text)
    {
        var circle = Create();
        var goal = circle.CreateSharedGoal(text);
        Assert.NotEqual(Guid.Empty, goal.Id);
        Assert.Equal(circle.Id, goal.CircleId);
        Assert.Equal(text, goal.Text);
        Assert.Equal(0, goal.ProgressPercent);
        Assert.Same(goal, Assert.Single(circle.Goals));
    }

    [Fact]
    public void MultipleGoals_HaveIndependentIdentitiesAndPreserveExistingGoals()
    {
        var circle = Create();
        var first = circle.CreateSharedGoal("First");
        var firstSnapshot = circle.Goals;
        var second = circle.CreateSharedGoal("Second");
        var third = circle.CreateSharedGoal("Third");
        Assert.Equal(3, circle.Goals.Count);
        Assert.Equal(3, circle.Goals.Select(goal => goal.Id).Distinct().Count());
        Assert.All(circle.Goals, goal => Assert.Equal(circle.Id, goal.CircleId));
        Assert.Same(first, circle.Goals[0]); Assert.Same(second, circle.Goals[1]); Assert.Same(third, circle.Goals[2]);
        Assert.Same(first, Assert.Single(firstSnapshot));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EditGoal_PreservesIdentityScopeAndEveryUnrelatedGoal(int selected)
    {
        var circle = Create();
        var originals = new[] { circle.CreateSharedGoal("One"), circle.CreateSharedGoal("Two"), circle.CreateSharedGoal("Three") };
        var snapshot = circle.Goals;
        var replacement = circle.EditSharedGoal(originals[selected].Id, "  Updated தமிழ்  ");
        Assert.Equal(originals[selected].Id, replacement.Id);
        Assert.Equal(circle.Id, replacement.CircleId);
        Assert.Equal("  Updated தமிழ்  ", replacement.Text);
        Assert.Equal(3, circle.Goals.Count);
        for (var i = 0; i < originals.Length; i++)
        {
            Assert.Same(originals[i], snapshot[i]);
            if (i == selected) Assert.Same(replacement, circle.Goals[i]);
            else Assert.Same(originals[i], circle.Goals[i]);
        }
        Assert.NotEqual(replacement.Text, originals[selected].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void InvalidText_CannotCreateOrEdit_AndPreservesAllExistingState(string? invalid)
    {
        var circle = Create();
        var first = circle.CreateSharedGoal("Keep first");
        var second = circle.CreateSharedGoal("Keep second");
        var snapshot = circle.Goals;
        var id = circle.Id;
        Assert.Throws<ArgumentException>("text", () => circle.CreateSharedGoal(invalid!));
        Assert.Throws<ArgumentException>("text", () => circle.EditSharedGoal(first.Id, invalid!));
        Assert.Same(snapshot, circle.Goals);
        Assert.Same(first, circle.Goals[0]); Assert.Same(second, circle.Goals[1]);
        Assert.Equal("Keep first", first.Text); Assert.Equal("Keep second", second.Text);
        Assert.Equal(id, circle.Id); Assert.Equal("learning", circle.Module);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidGoalReference_CannotEditOrInsert_AndPreservesState(bool empty)
    {
        var circle = Create();
        var goal = circle.CreateSharedGoal("Local");
        var snapshot = circle.Goals;
        if (empty) Assert.Throws<ArgumentException>("goalId", () => circle.EditSharedGoal(Guid.Empty, "Changed"));
        else Assert.Throws<KeyNotFoundException>(() => circle.EditSharedGoal(Guid.NewGuid(), "Changed"));
        Assert.Same(snapshot, circle.Goals);
        Assert.Same(goal, Assert.Single(circle.Goals));
        Assert.Equal("Local", goal.Text);
    }

    [Fact]
    public void OtherCircleGoal_CannotBeEditedOrImportedThroughItsIdentity()
    {
        var circle = Create(); var other = Create();
        circle.CreateSharedGoal("Local");
        var foreign = other.CreateSharedGoal("Foreign private goal");
        var snapshot = circle.Goals; var foreignSnapshot = other.Goals;
        Assert.Throws<KeyNotFoundException>(() => circle.EditSharedGoal(foreign.Id, "Changed"));
        Assert.Same(snapshot, circle.Goals); Assert.Same(foreignSnapshot, other.Goals);
        Assert.Equal("Foreign private goal", foreign.Text);
        // Local lookup isolation is not proof of persisted Circle authorization.
    }

    [Fact]
    public void GoalCollection_CannotBeAddedReplacedRemovedOrClearedExternally()
    {
        var circle = Create(); var goal = circle.CreateSharedGoal("Keep");
        var other = Create().CreateSharedGoal("Other");
        var list = Assert.IsAssignableFrom<IList<LearningCircle.SharedGoal>>(circle.Goals);
        Assert.Throws<NotSupportedException>(() => list.Add(other));
        Assert.Throws<NotSupportedException>(() => list[0] = other);
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Same(goal, Assert.Single(circle.Goals));
    }

    [Fact]
    public void EmptyCollection_IsAlsoUnmodifiable()
    {
        var circle = Create();
        var foreign = Create().CreateSharedGoal("Other");
        Assert.Throws<NotSupportedException>(() => ((IList<LearningCircle.SharedGoal>)circle.Goals).Add(foreign));
        Assert.Empty(circle.Goals);
    }

    [Fact]
    public void SameUnverifiedCircleId_DoesNotShareInMemoryState()
    {
        var circle = Create(); var other = new LearningCircle(circle.Id);
        circle.CreateSharedGoal("Private");
        Assert.Empty(other.Goals);
        // An ID is a reference, not membership, storage identity resolution or permission evidence.
    }

    [Fact]
    public void PublicSurface_PreservesImmutableIdentityAndIncludesOnlyApprovedLocalProgress()
    {
        foreach (var type in new[] { typeof(LearningCircle), typeof(LearningCircle.SharedGoal) })
        {
            Assert.True(type.IsSealed);
            Assert.Empty(type.GetFields(BindingFlags.Instance | BindingFlags.Public));
            Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
        }
        Assert.Empty(typeof(LearningCircle.SharedGoal).GetConstructors());
        Assert.Equal(new[] { "CreateSharedGoal", "EditSharedGoal", "ToString", "UpdateSharedGoalProgress" },
            typeof(LearningCircle).GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "CircleId", "Id", "ProgressPercent", "Text" },
            typeof(LearningCircle.SharedGoal).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        // Local reported progress grants no membership/roles, XP, connections, chat or goal deletion.
    }

    [Fact]
    public void Diagnostics_OmitPrivateIdentityAndContent()
    {
        var circle = Create(); var goal = circle.CreateSharedGoal("PRIVATE-SHARED-GOAL-MARKER");
        var diagnostic = circle.ToString() + goal;
        Assert.DoesNotContain(circle.Id.ToString(), diagnostic);
        Assert.DoesNotContain(goal.Id.ToString(), diagnostic);
        Assert.DoesNotContain(goal.Text, diagnostic);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProgressUpdates_PreserveTextScopeUnrelatedGoalsAndPreviousSnapshots(int selected)
    {
        var circle = Create();
        var originals = new[] { circle.CreateSharedGoal("First"), circle.CreateSharedGoal("  தமிழ் 👩🏽‍💻  "),
            circle.CreateSharedGoal("Third") };
        var initial = circle.Goals;
        foreach (var progress in new[] { 60, 100, 20, 0 })
        {
            var before = circle.Goals;
            var previous = before[selected];
            var replacement = circle.UpdateSharedGoalProgress(previous.Id, progress);
            Assert.Equal(progress, replacement.ProgressPercent);
            Assert.Equal(previous.Text, replacement.Text);
            Assert.Equal(previous.Id, replacement.Id); Assert.Equal(circle.Id, replacement.CircleId);
            Assert.Same(replacement, circle.Goals[selected]); Assert.NotSame(previous, replacement);
            Assert.Equal(3, circle.Goals.Count); Assert.Equal("learning", circle.Module);
            Assert.Same(previous, before[selected]);
            for (var i = 0; i < originals.Length; i++)
            {
                Assert.Same(originals[i], initial[i]); Assert.Equal(0, initial[i].ProgressPercent);
                if (i != selected) Assert.Same(originals[i], circle.Goals[i]);
            }
        }
    }

    [Fact]
    public void ProgressUpdate_AcceptsEveryIntegerFromZeroThroughOneHundred()
    {
        var circle = Create(); var goal = circle.CreateSharedGoal("Reported progress only");
        foreach (var progress in Enumerable.Range(0, 101))
            Assert.Equal(progress, circle.UpdateSharedGoalProgress(goal.Id, progress).ProgressPercent);
        Assert.Equal(0, goal.ProgressPercent);
        // The value records a report only; no achievement verification, XP or reward operation exists.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(100)]
    public void TextEdits_PreserveReportedProgressAndEarlierContent(int progress)
    {
        var circle = Create(); var goal = circle.CreateSharedGoal("Original text");
        var reported = circle.UpdateSharedGoalProgress(goal.Id, progress);
        var before = circle.Goals;
        var edited = circle.EditSharedGoal(goal.Id, "  Explicit new text  ");
        Assert.Equal(progress, edited.ProgressPercent);
        Assert.Equal("  Explicit new text  ", edited.Text);
        Assert.Equal(goal.Id, edited.Id); Assert.Equal(circle.Id, edited.CircleId);
        Assert.Same(reported, Assert.Single(before));
        Assert.Equal("Original text", reported.Text); Assert.Equal(progress, reported.ProgressPercent);
        Assert.Equal(0, goal.ProgressPercent);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void InvalidProgress_PreservesCompleteCircleState(int invalid)
    {
        var circle = Create(); var goal = circle.CreateSharedGoal("Keep text");
        circle.UpdateSharedGoalProgress(goal.Id, 45);
        var other = circle.CreateSharedGoal("Other text"); var before = circle.Goals;
        Assert.Throws<ArgumentOutOfRangeException>("progressPercent", () => circle.UpdateSharedGoalProgress(goal.Id, invalid));
        Assert.Same(before, circle.Goals); Assert.Same(other, circle.Goals[1]);
        Assert.Equal(45, circle.Goals[0].ProgressPercent); Assert.Equal("Keep text", circle.Goals[0].Text);
        Assert.Equal(0, other.ProgressPercent);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("foreign")]
    [InlineData("circle-id")]
    public void InvalidGoalReference_CannotUpdateProgressOrCreateGoals(string defect)
    {
        var circle = Create(); var goal = circle.CreateSharedGoal("Local");
        circle.UpdateSharedGoalProgress(goal.Id, 100);
        var other = Create(); var foreign = other.CreateSharedGoal("Foreign");
        var before = circle.Goals; var otherBefore = other.Goals;
        var id = defect switch
        {
            "empty" => Guid.Empty, "foreign" => foreign.Id, "circle-id" => circle.Id, _ => Guid.NewGuid()
        };
        if (defect == "empty")
            Assert.Throws<ArgumentException>("goalId", () => circle.UpdateSharedGoalProgress(id, 30));
        else Assert.Throws<KeyNotFoundException>(() => circle.UpdateSharedGoalProgress(id, 30));
        Assert.Same(before, circle.Goals); Assert.Same(otherBefore, other.Goals);
        Assert.Equal(100, Assert.Single(circle.Goals).ProgressPercent);
        Assert.Equal(0, foreign.ProgressPercent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void InvalidTextEdit_DoesNotResetExistingProgress(string? invalid)
    {
        var circle = Create(); var goal = circle.CreateSharedGoal("Keep text");
        circle.UpdateSharedGoalProgress(goal.Id, 75); var before = circle.Goals;
        Assert.Throws<ArgumentException>("text", () => circle.EditSharedGoal(goal.Id, invalid!));
        Assert.Same(before, circle.Goals);
        Assert.Equal(75, Assert.Single(circle.Goals).ProgressPercent);
        Assert.Equal("Keep text", circle.Goals[0].Text);
    }

    [Fact]
    public void NewGoalsStartAtZeroWithoutResettingOtherGoalsOrSharingStateByCircleId()
    {
        var circle = Create(); var goal = circle.CreateSharedGoal("Existing");
        var completedReport = circle.UpdateSharedGoalProgress(goal.Id, 100);
        var other = new LearningCircle(circle.Id); var otherGoal = other.CreateSharedGoal("Independent");
        var next = circle.CreateSharedGoal("New goal");
        Assert.Equal(0, next.ProgressPercent); Assert.Equal(100, completedReport.ProgressPercent);
        Assert.Same(completedReport, circle.Goals[0]); Assert.Equal(0, otherGoal.ProgressPercent);
        Assert.Same(otherGoal, Assert.Single(other.Goals));
        Assert.Throws<KeyNotFoundException>(() => other.UpdateSharedGoalProgress(goal.Id, 90));
        Assert.Equal(100, circle.Goals[0].ProgressPercent);
    }
}
