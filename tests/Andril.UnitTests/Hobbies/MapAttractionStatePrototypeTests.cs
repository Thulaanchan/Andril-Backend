using Andril.Hobbies.Map.Places;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapAttractionStatePrototypeTests
{
    private static readonly MapAttraction Attraction = MapAttraction.Create(Guid.NewGuid(), "Synthetic", 0, 0);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SnapshotRequiresExplicitStateAndPreservesCanonicalIdentity(bool active)
    {
        var state = MapAttractionStatePrototype.CreateSnapshot(Attraction, active, 7);
        Assert.Equal(Attraction.AttractionId, state.AttractionId);
        Assert.Equal(active, state.IsActive);
        Assert.Equal(7, state.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransitionChangesStateOnceAndLeavesOriginalSnapshotUnchanged(bool active)
    {
        var original = MapAttractionStatePrototype.CreateSnapshot(Attraction, active, 4);
        var changed = original.Transition(!active, 4);
        Assert.Equal(original.AttractionId, changed.AttractionId);
        Assert.Equal(!active, changed.IsActive);
        Assert.Equal(5, changed.Version);
        Assert.Equal(active, original.IsActive);
        Assert.Equal(4, original.Version);
        Assert.Same(changed, changed.Transition(!active, 5));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleVersionCannotBecomeAnIdempotentSuccessEvenForMatchingState(bool desired)
    {
        var state = MapAttractionStatePrototype.CreateSnapshot(Attraction, false, 2);
        Assert.Throws<InvalidOperationException>(() => state.Transition(desired, 1));
        Assert.Throws<InvalidOperationException>(() => state.Transition(desired, 3));
        Assert.False(state.IsActive);
        Assert.Equal(2, state.Version);
    }

    [Fact]
    public void InvalidInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => MapAttractionStatePrototype.CreateSnapshot(null!, true, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapAttractionStatePrototype.CreateSnapshot(Attraction, true, -1));
        var state = MapAttractionStatePrototype.CreateSnapshot(Attraction, true, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Transition(false, -1));
    }

    [Fact]
    public void VersionOverflowCannotWrapOrMutateTheSnapshot()
    {
        var state = MapAttractionStatePrototype.CreateSnapshot(Attraction, true, long.MaxValue);
        Assert.Same(state, state.Transition(true, long.MaxValue));
        Assert.Throws<OverflowException>(() => state.Transition(false, long.MaxValue));
        Assert.True(state.IsActive);
        Assert.Equal(long.MaxValue, state.Version);
    }
}
