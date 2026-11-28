using Andril.Common.Accounts.Lifecycle;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class AccountLifecyclePolicyTests
{
    // Cross-product acceptance matrix: same-state requests belong to the future
    // receipt/replay coordinator and are not new transitions. Suspension is admin-only.
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(0, 2, true)]
    [InlineData(0, 3, true)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(1, 3, false)]
    [InlineData(2, 0, true)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(2, 3, true)]
    [InlineData(3, 0, false)]
    [InlineData(3, 1, false)]
    [InlineData(3, 2, false)]
    [InlineData(3, 3, false)]
    public void OwnerTransitionMatrixPreservesSuspensionAndTerminalDeletion(int current, int target, bool allowed) =>
        Assert.Equal(allowed, AccountLifecyclePolicy.AllowsOwnerTransition(
            (AccountLifecycleState)current, (AccountLifecycleState)target));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(99, 3)]
    [InlineData(2, 99)]
    public void UnknownStatesNeverAuthorizeOwnerTransitions(int current, int target) =>
        Assert.False(AccountLifecyclePolicy.AllowsOwnerTransition(
            (AccountLifecycleState)current, (AccountLifecycleState)target));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    [InlineData(99, false)]
    public void NonActiveAndUnknownStatesDenyProtectedAccess(int state, bool allowed) =>
        Assert.Equal(allowed, AccountLifecyclePolicy.AllowsProtectedAccess((AccountLifecycleState)state));

    [Fact]
    public void AccountStateVocabularyExcludesRejectedProductStates() =>
        Assert.Equal(new[] { "Active", "Suspended", "Deactivated", "Deleted" },
            Enum.GetNames<AccountLifecycleState>());
}
