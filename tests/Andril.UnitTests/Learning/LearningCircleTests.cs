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
    public void PublicSurface_PreservesImmutableIdentityAndOmitsUnapprovedMembershipAndProgressOperations()
    {
        foreach (var type in new[] { typeof(LearningCircle), typeof(LearningCircle.SharedGoal) })
        {
            Assert.True(type.IsSealed);
            Assert.Empty(type.GetFields(BindingFlags.Instance | BindingFlags.Public));
            Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
        }
        Assert.Empty(typeof(LearningCircle.SharedGoal).GetConstructors());
        Assert.Equal(new[] { "CreateSharedGoal", "EditSharedGoal", "ToString" },
            typeof(LearningCircle).GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "CircleId", "Id", "Text" },
            typeof(LearningCircle.SharedGoal).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        // No membership/roles, progress percentages, XP, connections, chat grants or goal deletion APIs.
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
}
