using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialCandidateRotatorTests
{
    private sealed record SyntheticCandidateToken(string TokenId, string DummyLabel);

    private sealed class DummyCandidateRef
    {
        public string Name { get; }
        public DummyCandidateRef(string name) => Name = name;
    }

    #region A. Null and Offset Validation

    [Fact]
    public void Rotate_NullCandidates_ThrowsArgumentNullException_WithParamName()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            SocialCandidateRotator.Rotate<string>(null!, 0));

        Assert.Equal("candidates", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void Rotate_NegativeOffset_ThrowsArgumentOutOfRangeException_WithParamName(int negativeOffset)
    {
        var candidates = new[] { "A", "B", "C" };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialCandidateRotator.Rotate(candidates, negativeOffset));

        Assert.Equal("offset", ex.ParamName);
        Assert.Equal(negativeOffset, ex.ActualValue);
    }

    [Fact]
    public void Rotate_ZeroOffset_IsAccepted()
    {
        var candidates = new[] { "A", "B" };

        var result = SocialCandidateRotator.Rotate(candidates, 0);

        Assert.Equal(new[] { "A", "B" }, result);
    }

    [Fact]
    public void Rotate_LargeValidOffsets_DoNotThrow()
    {
        var candidates = new[] { "Alpha", "Beta", "Gamma" };

        var exception = Record.Exception(() => SocialCandidateRotator.Rotate(candidates, int.MaxValue));

        Assert.Null(exception);
    }

    #endregion

    #region B. Empty and Single-Element Collections

    [Fact]
    public void Rotate_EmptyCollection_Offset0_ReturnsEmptyReadOnlyList()
    {
        var empty = Array.Empty<string>();

        var result = SocialCandidateRotator.Rotate(empty, 0);

        Assert.Empty(result);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(result);
    }

    [Fact]
    public void Rotate_EmptyCollection_PositiveOffset_ReturnsEmptyReadOnlyList()
    {
        var empty = Array.Empty<string>();

        var result = SocialCandidateRotator.Rotate(empty, 5);

        Assert.Empty(result);
    }

    [Fact]
    public void Rotate_EmptyCollection_IntMaxValueOffset_ReturnsEmptyReadOnlyList()
    {
        var empty = Array.Empty<string>();

        var result = SocialCandidateRotator.Rotate(empty, int.MaxValue);

        Assert.Empty(result);
    }

    [Fact]
    public void Rotate_SingleElement_Offset0_PreservesItem()
    {
        var candidates = new[] { "Solo" };

        var result = SocialCandidateRotator.Rotate(candidates, 0);

        Assert.Single(result);
        Assert.Equal("Solo", result[0]);
    }

    [Fact]
    public void Rotate_SingleElement_Offset1_PreservesItem()
    {
        var candidates = new[] { "Solo" };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Single(result);
        Assert.Equal("Solo", result[0]);
    }

    [Fact]
    public void Rotate_SingleElement_IntMaxValue_PreservesItem()
    {
        var candidates = new[] { "Solo" };

        var result = SocialCandidateRotator.Rotate(candidates, int.MaxValue);

        Assert.Single(result);
        Assert.Equal("Solo", result[0]);
    }

    [Fact]
    public void Rotate_SingleElement_ReturnsDefensiveReadOnlySnapshot()
    {
        var original = new List<string> { "Initial" };

        var result = SocialCandidateRotator.Rotate(original, 0);

        original[0] = "Mutated";
        original.Add("Second");

        Assert.Single(result);
        Assert.Equal("Initial", result[0]);
    }

    #endregion

    #region C. Deterministic Left Rotation

    [Theory]
    [InlineData(0, new[] { "A", "B", "C", "D" })]
    [InlineData(1, new[] { "B", "C", "D", "A" })]
    [InlineData(2, new[] { "C", "D", "A", "B" })]
    [InlineData(3, new[] { "D", "A", "B", "C" })]
    [InlineData(4, new[] { "A", "B", "C", "D" })]
    [InlineData(5, new[] { "B", "C", "D", "A" })]
    [InlineData(7, new[] { "D", "A", "B", "C" })]
    [InlineData(8, new[] { "A", "B", "C", "D" })]
    public void Rotate_FourElements_OffsetTable_ProducesExpectedLeftRotation(int offset, string[] expected)
    {
        var candidates = new[] { "A", "B", "C", "D" };

        var result = SocialCandidateRotator.Rotate(candidates, offset);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Rotate_RepeatedCalls_ProducesIdenticalDeterministicSequence()
    {
        var candidates = new[] { "A", "B", "C", "D" };

        var run1 = SocialCandidateRotator.Rotate(candidates, 2);
        var run2 = SocialCandidateRotator.Rotate(candidates, 2);
        var run3 = SocialCandidateRotator.Rotate(candidates, 2);

        Assert.Equal(run1, run2);
        Assert.Equal(run2, run3);
        Assert.Equal(new[] { "C", "D", "A", "B" }, run1);
    }

    [Fact]
    public void Rotate_LeavesOriginalCollectionOrderUnmodified()
    {
        var candidates = new[] { "First", "Second", "Third", "Fourth" };
        var snapshotBefore = candidates.ToArray();

        _ = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Equal(snapshotBefore, candidates);
    }

    #endregion

    #region D. Overflow Safety and Boundaries

    [Fact]
    public void Rotate_IntMaxValueOffset_AppliesModuloCorrectlyWithoutOverflow()
    {
        // 2147483647 % 4 = 3
        var candidates = new[] { "A", "B", "C", "D" };

        var result = SocialCandidateRotator.Rotate(candidates, int.MaxValue);

        Assert.Equal(new[] { "D", "A", "B", "C" }, result);
    }

    [Fact]
    public void Rotate_OffsetEqualToCountMinusOne_RotatesCorrectly()
    {
        var candidates = new[] { "A", "B", "C", "D" };

        var result = SocialCandidateRotator.Rotate(candidates, candidates.Length - 1);

        Assert.Equal(new[] { "D", "A", "B", "C" }, result);
    }

    [Fact]
    public void Rotate_OffsetEqualToCount_ReturnsOriginalOrdering()
    {
        var candidates = new[] { "A", "B", "C", "D" };

        var result = SocialCandidateRotator.Rotate(candidates, candidates.Length);

        Assert.Equal(new[] { "A", "B", "C", "D" }, result);
    }

    [Fact]
    public void Rotate_OffsetEqualToCountPlusOne_RotatesByOne()
    {
        var candidates = new[] { "A", "B", "C", "D" };

        var result = SocialCandidateRotator.Rotate(candidates, candidates.Length + 1);

        Assert.Equal(new[] { "B", "C", "D", "A" }, result);
    }

    [Fact]
    public void Rotate_LargeMultipleOfCount_ReturnsOriginalOrdering()
    {
        var candidates = new[] { "A", "B", "C", "D" };
        var largeMultiple = candidates.Length * 100_000; // 400,000

        var result = SocialCandidateRotator.Rotate(candidates, largeMultiple);

        Assert.Equal(new[] { "A", "B", "C", "D" }, result);
    }

    [Fact]
    public void Rotate_TwoElements_EvenLength_RotatesCorrectly()
    {
        var candidates = new[] { 10, 20 };

        Assert.Equal(new[] { 10, 20 }, SocialCandidateRotator.Rotate(candidates, 0));
        Assert.Equal(new[] { 20, 10 }, SocialCandidateRotator.Rotate(candidates, 1));
        Assert.Equal(new[] { 10, 20 }, SocialCandidateRotator.Rotate(candidates, 2));
    }

    [Fact]
    public void Rotate_ThreeElements_OddLength_RotatesCorrectly()
    {
        var candidates = new[] { "X", "Y", "Z" };

        Assert.Equal(new[] { "X", "Y", "Z" }, SocialCandidateRotator.Rotate(candidates, 0));
        Assert.Equal(new[] { "Y", "Z", "X" }, SocialCandidateRotator.Rotate(candidates, 1));
        Assert.Equal(new[] { "Z", "X", "Y" }, SocialCandidateRotator.Rotate(candidates, 2));
        Assert.Equal(new[] { "X", "Y", "Z" }, SocialCandidateRotator.Rotate(candidates, 3));
    }

    [Fact]
    public void Rotate_FiveElements_OddLength_RotatesCorrectly()
    {
        var candidates = new[] { 1, 2, 3, 4, 5 };

        Assert.Equal(new[] { 3, 4, 5, 1, 2 }, SocialCandidateRotator.Rotate(candidates, 2));
        Assert.Equal(new[] { 5, 1, 2, 3, 4 }, SocialCandidateRotator.Rotate(candidates, 4));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, SocialCandidateRotator.Rotate(candidates, 5));
    }

    [Fact]
    public void Rotate_SixElements_EvenLength_RotatesCorrectly()
    {
        var candidates = new[] { "a", "b", "c", "d", "e", "f" };

        Assert.Equal(new[] { "d", "e", "f", "a", "b", "c" }, SocialCandidateRotator.Rotate(candidates, 3));
        Assert.Equal(new[] { "b", "c", "d", "e", "f", "a" }, SocialCandidateRotator.Rotate(candidates, 7)); // 7 % 6 = 1
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 999)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(4, 7)]
    [InlineData(10, 0)]
    [InlineData(10, 5)]
    [InlineData(10, 23)]
    [InlineData(10, int.MaxValue)]
    public void Rotate_VariousLengthsAndOffsets_NeverThrowsIndexOutOfRangeException(int count, int offset)
    {
        var items = Enumerable.Range(0, count).ToArray();

        var exception = Record.Exception(() => SocialCandidateRotator.Rotate(items, offset));

        Assert.Null(exception);
    }

    #endregion

    #region E. Item and Reference Preservation

    [Fact]
    public void Rotate_PreservesAllElementsAndFrequencies()
    {
        var candidates = new[] { "Item1", "Item2", "Item3", "Item4" };

        var result = SocialCandidateRotator.Rotate(candidates, 3);

        Assert.Equal(candidates.Length, result.Count);
        foreach (var item in candidates)
        {
            Assert.Equal(
                candidates.Count(c => c == item),
                result.Count(r => r == item));
        }
    }

    [Fact]
    public void Rotate_WithDuplicates_PreservesAllDuplicateValuesInRotatedOrder()
    {
        var candidates = new[] { "A", "A", "B", "A", "C" };

        var result = SocialCandidateRotator.Rotate(candidates, 2);

        // Input: A0, A1, B, A2, C
        // Rotated by 2: B, A2, C, A0, A1
        Assert.Equal(new[] { "B", "A", "C", "A", "A" }, result);
        Assert.Equal(3, result.Count(x => x == "A"));
        Assert.Equal(1, result.Count(x => x == "B"));
        Assert.Equal(1, result.Count(x => x == "C"));
    }

    [Fact]
    public void Rotate_WithRepeatedReferences_PreservesRepeatedReferences()
    {
        var sharedRef = new DummyCandidateRef("Shared");
        var otherRef = new DummyCandidateRef("Other");
        var candidates = new[] { sharedRef, otherRef, sharedRef };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        // Input: [sharedRef, otherRef, sharedRef]
        // Rotated by 1: [otherRef, sharedRef, sharedRef]
        Assert.Same(otherRef, result[0]);
        Assert.Same(sharedRef, result[1]);
        Assert.Same(sharedRef, result[2]);
    }

    [Fact]
    public void Rotate_ReferenceTypeElements_PreservesExactReferenceIdentity()
    {
        var r1 = new DummyCandidateRef("R1");
        var r2 = new DummyCandidateRef("R2");
        var r3 = new DummyCandidateRef("R3");
        var candidates = new[] { r1, r2, r3 };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Same(r2, result[0]);
        Assert.Same(r3, result[1]);
        Assert.Same(r1, result[2]);
    }

    [Fact]
    public void Rotate_NullableReferenceTypes_PreservesNullAndNonNullElements()
    {
        var candidates = new string?[] { null, "Alpha", null, "Beta" };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Equal(new string?[] { "Alpha", null, "Beta", null }, result);
    }

    [Fact]
    public void Rotate_DoesNotSortElements()
    {
        var unsorted = new[] { 99, 12, 54, 3, 77 };

        var result = SocialCandidateRotator.Rotate(unsorted, 2);

        // Rotated by 2: [54, 3, 77, 99, 12]
        Assert.Equal(new[] { 54, 3, 77, 99, 12 }, result);
        Assert.False(result.SequenceEqual(unsorted.OrderBy(x => x)));
    }

    [Fact]
    public void Rotate_DoesNotDeduplicateElements()
    {
        var duplicates = new[] { "Duplicate", "Duplicate", "Duplicate" };

        var result = SocialCandidateRotator.Rotate(duplicates, 1);

        Assert.Equal(3, result.Count);
        Assert.All(result, item => Assert.Equal("Duplicate", item));
    }

    #endregion

    #region F. Defensive Immutability

    [Fact]
    public void Rotate_ReturnsReadOnlyCollection()
    {
        var candidates = new[] { "A", "B", "C" };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        var ilist = (IList<string>)result;
        Assert.True(ilist.IsReadOnly);
    }

    [Fact]
    public void Rotate_ReturnsNewInstance_NotOriginalListInstance()
    {
        var originalList = new List<string> { "A", "B", "C" };

        var result = SocialCandidateRotator.Rotate(originalList, 0);

        Assert.NotSame(originalList, result);
    }

    [Fact]
    public void Rotate_DoesNotExposeMutableUnderlyingArray()
    {
        var candidates = new[] { "A", "B", "C" };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Null(result as string[]);
    }

    [Fact]
    public void Rotate_MutatingInputListAfterRotation_DoesNotAlterResult()
    {
        var original = new List<string> { "Original0", "Original1", "Original2" };

        var result = SocialCandidateRotator.Rotate(original, 1);

        original[0] = "Mutated0";
        original[1] = "Mutated1";

        Assert.Equal("Original1", result[0]);
        Assert.Equal("Original2", result[1]);
        Assert.Equal("Original0", result[2]);
    }

    [Fact]
    public void Rotate_MutatingInputArrayAfterRotation_DoesNotAlterResult()
    {
        var original = new[] { "A", "B", "C" };

        var result = SocialCandidateRotator.Rotate(original, 1);

        original[0] = "Modified";

        Assert.Equal("B", result[0]);
        Assert.Equal("C", result[1]);
        Assert.Equal("A", result[2]);
    }

    [Fact]
    public void Rotate_AddingToOriginalInputList_DoesNotChangeResultCount()
    {
        var original = new List<string> { "A", "B" };

        var result = SocialCandidateRotator.Rotate(original, 1);

        original.Add("C");
        original.Add("D");

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "B", "A" }, result);
    }

    [Fact]
    public void Rotate_RemovingFromOriginalInputList_DoesNotChangeResult()
    {
        var original = new List<string> { "A", "B", "C" };

        var result = SocialCandidateRotator.Rotate(original, 1);

        original.RemoveAt(0);

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "B", "C", "A" }, result);
    }

    [Fact]
    public void Rotate_AttemptingIListAdd_ThrowsNotSupportedException()
    {
        var result = SocialCandidateRotator.Rotate(new[] { "A", "B" }, 1);
        var ilist = (IList<string>)result;

        Assert.Throws<NotSupportedException>(() => ilist.Add("NewItem"));
    }

    [Fact]
    public void Rotate_AttemptingIListRemove_ThrowsNotSupportedException()
    {
        var result = SocialCandidateRotator.Rotate(new[] { "A", "B" }, 1);
        var ilist = (IList<string>)result;

        Assert.Throws<NotSupportedException>(() => ilist.Remove("A"));
    }

    [Fact]
    public void Rotate_AttemptingIListClear_ThrowsNotSupportedException()
    {
        var result = SocialCandidateRotator.Rotate(new[] { "A", "B" }, 1);
        var ilist = (IList<string>)result;

        Assert.Throws<NotSupportedException>(() => ilist.Clear());
    }

    [Fact]
    public void Rotate_AttemptingIListIndexerSet_ThrowsNotSupportedException()
    {
        var result = SocialCandidateRotator.Rotate(new[] { "A", "B" }, 1);
        var ilist = (IList<string>)result;

        Assert.Throws<NotSupportedException>(() => ilist[0] = "Overwritten");
    }

    [Fact]
    public void Rotate_MultipleCalls_ReturnIndependentSnapshotInstances()
    {
        var candidates = new[] { "A", "B", "C" };

        var result1 = SocialCandidateRotator.Rotate(candidates, 1);
        var result2 = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.NotSame(result1, result2);
        Assert.Equal(result1, result2);
    }

    #endregion

    #region G. Generic Type Compatibility

    [Fact]
    public void Rotate_Strings_RotatesCorrectly()
    {
        var candidates = new[] { "One", "Two", "Three" };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Equal(new[] { "Two", "Three", "One" }, result);
    }

    [Fact]
    public void Rotate_Integers_RotatesCorrectly()
    {
        var candidates = new[] { 100, 200, 300, 400 };

        var result = SocialCandidateRotator.Rotate(candidates, 2);

        Assert.Equal(new[] { 300, 400, 100, 200 }, result);
    }

    [Fact]
    public void Rotate_Guids_RotatesCorrectly()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var g3 = Guid.NewGuid();
        var candidates = new[] { g1, g2, g3 };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Equal(new[] { g2, g3, g1 }, result);
    }

    [Fact]
    public void Rotate_SyntheticCandidateRecords_RotatesCorrectly()
    {
        var t1 = new SyntheticCandidateToken("token-1", "Alice");
        var t2 = new SyntheticCandidateToken("token-2", "Bob");
        var t3 = new SyntheticCandidateToken("token-3", "Charlie");
        var candidates = new[] { t1, t2, t3 };

        var result = SocialCandidateRotator.Rotate(candidates, 2);

        Assert.Equal(new[] { t3, t1, t2 }, result);
    }

    [Fact]
    public void Rotate_RepeatedObjectReferences_PreservesIdentity()
    {
        var instance = new SyntheticCandidateToken("token-x", "Repeated");
        var candidates = new[] { instance, instance };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Same(instance, result[0]);
        Assert.Same(instance, result[1]);
    }

    [Fact]
    public void Rotate_NullableStrings_RotatesCorrectly()
    {
        var candidates = new string?[] { "First", null, "Third" };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Equal(new string?[] { null, "Third", "First" }, result);
    }

    #endregion

    #region H. Determinism and Statelessness

    [Fact]
    public void Rotate_IsDeterministicAcrossIndependentInvocations()
    {
        var candidates = new[] { "X", "Y", "Z", "W" };

        var firstPass = SocialCandidateRotator.Rotate(candidates, 3);
        var secondPass = SocialCandidateRotator.Rotate(candidates, 3);

        Assert.Equal(firstPass, secondPass);
    }

    [Fact]
    public void Rotate_HasNoStaticSideEffectsAcrossDifferentCollections()
    {
        var listA = new[] { 1, 2, 3 };
        var listB = new[] { 10, 20, 30 };

        var resultA1 = SocialCandidateRotator.Rotate(listA, 1);
        var resultB = SocialCandidateRotator.Rotate(listB, 2);
        var resultA2 = SocialCandidateRotator.Rotate(listA, 1);

        Assert.Equal(new[] { 2, 3, 1 }, resultA1);
        Assert.Equal(new[] { 30, 10, 20 }, resultB);
        Assert.Equal(resultA1, resultA2);
    }

    [Fact]
    public void Rotate_EmptyAndNonEmptyInvocationsDoNotInterfere()
    {
        var empty = Array.Empty<int>();
        var nonEmpty = new[] { 1, 2, 3 };

        _ = SocialCandidateRotator.Rotate(empty, 5);
        var resultNonEmpty = SocialCandidateRotator.Rotate(nonEmpty, 1);
        var resultEmpty = SocialCandidateRotator.Rotate(empty, 0);

        Assert.Equal(new[] { 2, 3, 1 }, resultNonEmpty);
        Assert.Empty(resultEmpty);
    }

    [Fact]
    public void Rotate_DoesNotDependOnSystemClockOrEnvironment()
    {
        var candidates = new[] { "Alpha", "Beta", "Gamma" };

        var r1 = SocialCandidateRotator.Rotate(candidates, 1);
        var r2 = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Equal(r1, r2);
    }

    #endregion

    #region I. Privacy and Security Scope Verification

    [Fact]
    public void Rotate_OpaqueCandidateTokens_OperatesWithoutDemographicOrIdentityInspection()
    {
        var candidate1 = new SyntheticCandidateToken("opaque-id-1", "Candidate 1");
        var candidate2 = new SyntheticCandidateToken("opaque-id-2", "Candidate 2");
        var candidates = new[] { candidate1, candidate2 };

        var result = SocialCandidateRotator.Rotate(candidates, 1);

        Assert.Same(candidate2, result[0]);
        Assert.Same(candidate1, result[1]);
    }

    [Fact]
    public void Rotate_DoesNotRequireAuthenticationOrAuthorizationContext()
    {
        // Demonstrates that SocialCandidateRotator is a pure mathematical ordering primitive
        // and does not require HttpContext, ICurrentUser, or IPrivateModuleAccess.
        var tokens = new[] { "token_abc", "token_def", "token_ghi" };

        var result = SocialCandidateRotator.Rotate(tokens, 2);

        Assert.Equal(new[] { "token_ghi", "token_abc", "token_def" }, result);
    }

    [Fact]
    public void Rotate_TreatsElementsAsBlackBoxValues()
    {
        var dummyItems = new object[] { "StringValue", 42, Guid.Empty };

        var result = SocialCandidateRotator.Rotate(dummyItems, 1);

        Assert.Equal(42, result[0]);
        Assert.Equal(Guid.Empty, result[1]);
        Assert.Equal("StringValue", result[2]);
    }

    #endregion

    #region J. Moderate Collection Scenario

    [Fact]
    public void Rotate_ModerateCollection1000Items_PreservesCount()
    {
        var source = Enumerable.Range(0, 1000).ToArray();

        var result = SocialCandidateRotator.Rotate(source, 250);

        Assert.Equal(1000, result.Count);
    }

    [Fact]
    public void Rotate_ModerateCollection1000Items_OrdersCorrectly()
    {
        var source = Enumerable.Range(0, 1000).ToArray();
        const int offset = 350;

        var result = SocialCandidateRotator.Rotate(source, offset);

        // Elements 350..999 should be at positions 0..649
        for (var i = 0; i < 650; i++)
        {
            Assert.Equal(350 + i, result[i]);
        }

        // Elements 0..349 should be at positions 650..999
        for (var i = 0; i < 350; i++)
        {
            Assert.Equal(i, result[650 + i]);
        }
    }

    [Fact]
    public void Rotate_ModerateCollection1000Items_PreservesAllItemsWithoutDuplicates()
    {
        var source = Enumerable.Range(0, 1000).ToArray();

        var result = SocialCandidateRotator.Rotate(source, 777);

        var distinctCount = result.Distinct().Count();
        Assert.Equal(1000, distinctCount);
        Assert.Equal(source.ToHashSet(), result.ToHashSet());
    }

    [Fact]
    public void Rotate_ModerateCollection1000Items_LeavesSourceUnchanged()
    {
        var source = Enumerable.Range(0, 1000).ToArray();
        var snapshot = source.ToArray();

        _ = SocialCandidateRotator.Rotate(source, 123);

        Assert.Equal(snapshot, source);
    }

    [Fact]
    public void Rotate_ModerateCollection1000Items_WithLargeOffset_ComputesCorrectly()
    {
        var source = Enumerable.Range(0, 1000).ToArray();
        // offset 2500 % 1000 = 500
        const int largeOffset = 2500;

        var result = SocialCandidateRotator.Rotate(source, largeOffset);

        Assert.Equal(500, result[0]);
        Assert.Equal(999, result[499]);
        Assert.Equal(0, result[500]);
        Assert.Equal(499, result[999]);
    }

    #endregion
}
