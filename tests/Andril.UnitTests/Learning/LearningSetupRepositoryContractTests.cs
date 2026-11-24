using System.Text.Json;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningSetupRepositoryContractTests
{
    private static LearningPreferences Preferences() => new(Guid.NewGuid(), Guid.NewGuid());
    private static LearningSetupScope Scope(LearningPreferences p) => new(p.AccountId, p.LearningProfileId);
    private static LearningPersonalGoal Goal(LearningPreferences p) =>
        LearningPersonalGoal.CreateFromPreferences(p, p.CreateGoal("PRIVATE-SETUP-GOAL").Id);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScopeRejectsEmptyReferences(bool emptyAccount) =>
        Assert.Throws<ArgumentException>(() => new LearningSetupScope(emptyAccount ? Guid.Empty : Guid.NewGuid(),
            emptyAccount ? Guid.NewGuid() : Guid.Empty));

    [Fact]
    public void SnapshotCopiesPrivateScalarsAndPreservesAllowedCrossPurposeOverlap()
    {
        var p = Preferences(); var skill = Guid.NewGuid(); p.UpdateSelections([], [skill], [skill]);
        var rows = new List<LearningSkillSelection>
        {
            LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, skill, LearningSkillSelectionKind.Teaching),
            LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, skill, LearningSkillSelectionKind.Learning)
        };
        var goals = new List<LearningPersonalGoal> { Goal(p) };
        var snapshot = new LearningSetupSnapshot(Scope(p), rows, goals);
        rows.Clear(); goals.Clear();
        Assert.Equal(2, snapshot.Selections.Count);
        Assert.All(snapshot.Selections, row => Assert.Equal(skill, row.SkillId));
        Assert.Equal("PRIVATE-SETUP-GOAL", Assert.Single(snapshot.Goals).Text);
        Assert.Equal(p.AccountId, snapshot.Scope.AccountId); Assert.Equal(p.LearningProfileId, snapshot.Scope.LearningProfileId);
        Assert.Equal("learning", snapshot.Scope.Module);
        Assert.Throws<NotSupportedException>(() => ((IList<LearningSetupSnapshot.Selection>)snapshot.Selections).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<LearningSetupSnapshot.Goal>)snapshot.Goals).Clear());
    }

    [Theory]
    [InlineData("foreign-selection")]
    [InlineData("foreign-goal")]
    [InlineData("null-selection")]
    [InlineData("null-goal")]
    [InlineData("duplicate-selection-id")]
    [InlineData("duplicate-membership")]
    [InlineData("duplicate-goal")]
    public void InvalidScopedRowsCannotBecomeSnapshots(string defect)
    {
        var p = Preferences(); var foreign = Preferences(); var skill = Guid.NewGuid();
        p.UpdateSelections([], [skill], []); foreign.UpdateSelections([], [skill], []);
        var row = LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, skill, LearningSkillSelectionKind.Teaching);
        var rows = new List<LearningSkillSelection> { row }; var goal = Goal(p); var goals = new List<LearningPersonalGoal> { goal };
        switch (defect)
        {
            case "foreign-selection": rows.Add(LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), foreign, skill, LearningSkillSelectionKind.Teaching)); break;
            case "foreign-goal": goals.Add(Goal(foreign)); break;
            case "null-selection": rows.Add(null!); break;
            case "null-goal": goals.Add(null!); break;
            case "duplicate-selection-id": rows.Add(row); break;
            case "duplicate-membership": rows.Add(LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), p, skill, LearningSkillSelectionKind.Teaching)); break;
            case "duplicate-goal": goals.Add(goal); break;
        }
        var error = Assert.Throws<ArgumentException>(() => new LearningSetupSnapshot(Scope(p), rows, goals));
        Assert.DoesNotContain(p.LearningProfileId.ToString(), error.ToString());
        Assert.DoesNotContain("PRIVATE-SETUP-GOAL", error.ToString());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("selections")]
    [InlineData("goals")]
    public void MissingSnapshotInputsAreRejected(string missing)
    {
        var p = Preferences();
        Assert.Throws<ArgumentNullException>(() => new LearningSetupSnapshot(missing == "scope" ? null! : Scope(p),
            missing == "selections" ? null! : [], missing == "goals" ? null! : []));
    }

    [Fact]
    public void DeniedAndUnavailableAreNotFabricatedAvailableEmptyData()
    {
        var p = Preferences(); var snapshot = new LearningSetupSnapshot(Scope(p), [], []);
        var available = LearningSetupReadResult.Available(snapshot, " opaque revision ");
        Assert.Equal(LearningSetupReadState.Available, available.State); Assert.Same(snapshot, available.Snapshot);
        Assert.Equal(" opaque revision ", available.StorageRevision);
        foreach (var result in new[] { LearningSetupReadResult.Denied(), LearningSetupReadResult.Unavailable() })
        {
            Assert.NotEqual(LearningSetupReadState.Available, result.State);
            Assert.Null(result.Snapshot); Assert.Null(result.StorageRevision);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void AvailableRequiresOpaqueRevision(string? revision) =>
        Assert.Throws<ArgumentException>(() => LearningSetupReadResult.Available(new(Scope(Preferences()), [], []), revision!));

    [Fact]
    public void AvailableRequiresSnapshot() =>
        Assert.Throws<ArgumentNullException>(() => LearningSetupReadResult.Available(null!, "revision"));

    [Fact]
    public void JsonAndDiagnosticsDoNotExposeScopeContentOrRevision()
    {
        var p = Preferences(); var goal = Goal(p);
        var snapshot = new LearningSetupSnapshot(Scope(p), [], [goal]);
        var result = LearningSetupReadResult.Available(snapshot, "PRIVATE-REVISION");
        foreach (var value in new object[] { snapshot.Scope, snapshot, snapshot.Goals[0], result })
        {
            Assert.Equal("{}", JsonSerializer.Serialize(value, value.GetType()));
            Assert.DoesNotContain("PRIVATE-SETUP-GOAL", value.ToString());
            Assert.DoesNotContain("PRIVATE-REVISION", value.ToString());
            Assert.DoesNotContain(p.LearningProfileId.ToString(), value.ToString());
        }
    }

    [Fact]
    public void ContractRequiresCancellationAndDoesNotSupplyAnAuthorityOrDefaultImplementation()
    {
        var contract = typeof(ILearningSetupRepository);
        Assert.True(contract.IsInterface);
        Assert.Equal(new[] { "ReadAsync", "ReplaceAsync" }, contract.GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.All(contract.GetMethods(), method =>
        {
            Assert.True(method.IsAbstract);
            Assert.Equal(typeof(CancellationToken), method.GetParameters()[^1].ParameterType);
            Assert.True(method.GetParameters()[^1].HasDefaultValue);
        });
        Assert.Equal(typeof(Task<LearningSetupReadResult>), contract.GetMethod("ReadAsync")!.ReturnType);
        Assert.Equal(typeof(Task<LearningSetupWriteOutcome>), contract.GetMethod("ReplaceAsync")!.ReturnType);
        Assert.Contains(LearningSetupWriteOutcome.CommitUnknown, Enum.GetValues<LearningSetupWriteOutcome>());
        // Metadata and envelope tests do not prove a future adapter's authorization, transactions,
        // cancellation, revocation, catalogue freshness or PostgreSQL concurrency behavior.
    }
}
