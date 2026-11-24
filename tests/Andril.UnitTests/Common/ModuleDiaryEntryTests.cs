using System.Reflection;
using System.Text.Json;
using Andril.Common.Diary;
using Andril.Common.Accounts.StarterModules;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ModuleDiaryEntryTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherOwner = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly Day = new(2026, 11, 21);
    private const string ReflectionTitle = "PRIVATE-REFLECTION-TITLE";
    private const string ReflectionBody = "PRIVATE-REFLECTION-BODY";

    [Theory]
    [InlineData(StarterModules.Learning)]
    [InlineData(StarterModules.Hobbies)]
    [InlineData(StarterModules.Social)]
    public void ValidModule_CreatesPrivateFixedDailyScopeWithoutContent(string module)
    {
        var entry = new ModuleDiaryEntry(Owner, module, Day);
        Assert.Equal(Owner, entry.OwnerModuleProfileId);
        Assert.Equal(module, entry.Module);
        Assert.Equal(Day, entry.Date);
        Assert.True(entry.IsPrivate);
        Assert.Null(entry.Reflection);
        Assert.Empty(entry.Plans);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("dating")]
    [InlineData("Learning")]
    [InlineData("unknown")]
    public void UnsupportedModule_IsRejected(string? module) =>
        Assert.Throws<ArgumentException>("module", () => new ModuleDiaryEntry(Owner, module!, Day));

    [Fact]
    public void EmptyOwner_IsRejected() =>
        Assert.Throws<ArgumentException>("ownerModuleProfileId", () => new ModuleDiaryEntry(Guid.Empty, "learning", Day));

    [Fact]
    public void ScopeAndContentSurface_ProvideNoPublicMutationBypass()
    {
        var type = typeof(ModuleDiaryEntry);
        Assert.True(type.IsSealed);
        foreach (var name in new[] { "OwnerModuleProfileId", "Module", "Date", "IsPrivate" })
            Assert.Null(type.GetProperty(name)!.SetMethod);
        Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        foreach (var contentType in new[] { typeof(DiaryReflection), typeof(DiaryPlan) })
        {
            Assert.True(contentType.IsSealed);
            Assert.All(contentType.GetProperties(), property => Assert.Null(property.SetMethod));
            Assert.Empty(contentType.GetFields(BindingFlags.Public | BindingFlags.Instance));
        }
    }

    [Theory]
    [InlineData("learning", "hobbies")]
    [InlineData("hobbies", "social")]
    [InlineData("social", "learning")]
    public void SeparateProfileAndModuleInstances_DoNotPropagateEdits(string firstModule, string secondModule)
    {
        var first = Populated(firstModule);
        var other = new ModuleDiaryEntry(OtherOwner, secondModule, Day);
        other.AddPlan(first.Plans[0]); // Immutable value sharing is safe; collections remain independent.
        other.SetReflection("Other title", "Other body");
        var before = Capture(other);
        first.EditPlan(first.Plans[0].Id, "Changed", null);
        first.EditReflection(null, "Changed body");
        first.RemovePlan(first.Plans[1].Id);
        first.RemoveReflection();
        AssertUnchanged(other, before);
        Assert.NotSame(first.Plans, other.Plans);
        Assert.Equal(OtherOwner, other.OwnerModuleProfileId);
        Assert.Equal(secondModule, other.Module);
    }

    [Fact]
    public void DifferentSelectedDates_RemainIndependentWithoutAutomaticRollover()
    {
        var first = Populated();
        var next = new ModuleDiaryEntry(Owner, "learning", Day.AddDays(1));
        first.RemoveReflection();
        first.RemovePlan(first.Plans[0].Id);
        Assert.Equal(Day, first.Date);
        Assert.Equal(Day.AddDays(1), next.Date);
        Assert.Empty(next.Plans);
        Assert.Null(next.Reflection);
    }

    [Fact]
    public void ReflectionSetReplaceEditAndRemoval_KeepPlansAndPreviousValuesIndependent()
    {
        var entry = Empty();
        entry.AddPlan(Plan(1)); entry.AddPlan(Plan(2));
        var plans = entry.Plans;
        entry.SetReflection(ReflectionTitle, ReflectionBody);
        var initial = entry.Reflection!;
        Assert.Equal(ReflectionTitle, initial.Title);
        Assert.Equal(ReflectionBody, initial.Body);
        entry.SetReflection(null, "Explicit replacement");
        var replacement = entry.Reflection!;
        Assert.NotSame(initial, replacement);
        Assert.Null(replacement.Title);
        Assert.Equal("Explicit replacement", replacement.Body);
        entry.EditReflection("Edited title", "Edited body");
        Assert.NotSame(replacement, entry.Reflection);
        Assert.Equal("Edited title", entry.Reflection!.Title);
        Assert.Equal("Edited body", entry.Reflection.Body);
        Assert.Equal(ReflectionTitle, initial.Title);
        Assert.Equal(ReflectionBody, initial.Body);
        Assert.Equal("Explicit replacement", replacement.Body);
        Assert.Same(plans, entry.Plans);
        entry.RemoveReflection();
        Assert.Null(entry.Reflection);
        Assert.Same(plans, entry.Plans);
        entry.RemoveReflection(); // Existing absence is an idempotent in-memory removal.
        Assert.Null(entry.Reflection);
        Assert.Same(plans, entry.Plans);
        AssertScope(entry);
    }

    [Fact]
    public void EditingAbsentReflection_IsRejectedWithoutCreatingContent()
    {
        var entry = Empty(); entry.AddPlan(Plan(1));
        var before = Capture(entry);
        Assert.Throws<InvalidOperationException>(() => entry.EditReflection("New title", "New body"));
        AssertUnchanged(entry, before);
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(false, "empty")]
    [InlineData(false, "blank")]
    [InlineData(false, "title-limit")]
    [InlineData(false, "body-limit")]
    [InlineData(true, "null")]
    [InlineData(true, "empty")]
    [InlineData(true, "blank")]
    [InlineData(true, "title-limit")]
    [InlineData(true, "body-limit")]
    public void InvalidReflectionUpdate_PreservesCompletePopulatedState(bool edit, string defect)
    {
        var entry = Populated(); var before = Capture(entry);
        var title = defect == "title-limit" ? new string('x', 101) : "Changed title";
        string? body = defect switch { "null" => null, "empty" => "", "blank" => " \t\r\n", "body-limit" => new string('x', 10_001), _ => "Changed body" };
        var parameter = defect == "title-limit" ? "title" : "body";
        var error = Assert.Throws<ArgumentException>(parameter, () =>
        {
            if (edit) entry.EditReflection(title, body!); else entry.SetReflection(title, body!);
        });
        AssertPrivateError(error, before);
        AssertUnchanged(entry, before);
    }

    [Fact]
    public void AddingPlans_PreservesExistingObjectsAndEarlierSnapshotsWithoutReflection()
    {
        var entry = Empty(); var empty = entry.Plans;
        var first = Plan(1); var second = Plan(2);
        entry.AddPlan(first); var one = entry.Plans;
        entry.AddPlan(second);
        Assert.Empty(empty);
        Assert.Same(first, Assert.Single(one));
        Assert.Equal(new[] { first, second }, entry.Plans);
        Assert.Same(first, entry.Plans[0]); Assert.Same(second, entry.Plans[1]);
        Assert.Null(entry.Reflection);
        AssertScope(entry);
    }

    [Fact]
    public void EqualPlanTextWithDistinctIdentities_IsAllowed()
    {
        var entry = Empty();
        entry.AddPlan(new DiaryPlan(Plan(1).Id, "Same title", "Same description"));
        entry.AddPlan(new DiaryPlan(Plan(2).Id, "Same title", "Same description"));
        Assert.Equal(2, entry.Plans.Count);
        Assert.NotEqual(entry.Plans[0].Id, entry.Plans[1].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicatePlanIdentity_IsRejectedAndAllStatePreserved(bool differentInstance)
    {
        var entry = Populated(); var before = Capture(entry);
        var duplicate = differentInstance ? new DiaryPlan(entry.Plans[0].Id, "PRIVATE-DUPLICATE", null) : entry.Plans[0];
        var error = Assert.Throws<ArgumentException>("plan", () => entry.AddPlan(duplicate));
        AssertPrivateError(error, before);
        AssertUnchanged(entry, before);
    }

    [Fact]
    public void NullPlan_IsRejectedWithoutMutation()
    {
        var entry = Populated(); var before = Capture(entry);
        Assert.Throws<ArgumentNullException>("plan", () => entry.AddPlan(null!));
        AssertUnchanged(entry, before);
    }

    [Fact]
    public void EmptyPlanIdentity_CannotBeConstructedOrAdded()
    {
        var entry = Populated(); var before = Capture(entry);
        Assert.Throws<ArgumentException>("id", () => entry.AddPlan(new DiaryPlan(Guid.Empty, "Private title")));
        AssertUnchanged(entry, before);
    }

    [Fact]
    public void TwentyPlansAccepted_TwentyFirstRejectedWithoutPartialMutation()
    {
        var entry = Populated();
        for (var i = 4; i <= 20; i++) entry.AddPlan(Plan(i));
        Assert.Equal(20, entry.Plans.Count);
        Assert.Equal(20, entry.Plans.Select(plan => plan.Id).Distinct().Count());
        var before = Capture(entry);
        var error = Assert.Throws<InvalidOperationException>(() => entry.AddPlan(Plan(21)));
        AssertPrivateError(error, before);
        AssertUnchanged(entry, before);
        entry.RemovePlan(entry.Plans[0].Id);
        entry.AddPlan(Plan(21));
        Assert.Equal(20, entry.Plans.Count);
        Assert.Equal(Plan(21).Id, entry.Plans[^1].Id);
        Assert.Same(before.Reflection, entry.Reflection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EditingSelectedPlan_PreservesIdentityOrderReflectionAndUnrelatedPlans(int index)
    {
        var entry = Populated(); var before = Capture(entry);
        var id = entry.Plans[index].Id;
        entry.EditPlan(id, "  Edited title e\u0301 👩‍💻  ", "Edited description");
        Assert.NotSame(before.Plans, entry.Plans);
        Assert.Equal(before.Plans.Select(plan => plan.Id), entry.Plans.Select(plan => plan.Id));
        Assert.Equal(id, entry.Plans[index].Id);
        Assert.Equal("  Edited title e\u0301 👩‍💻  ", entry.Plans[index].Title);
        Assert.Equal("Edited description", entry.Plans[index].Description);
        for (var i = 0; i < before.Plans.Count; i++) if (i != index) Assert.Same(before.Plans[i], entry.Plans[i]);
        Assert.Same(before.Reflection, entry.Reflection);
        AssertCapturedContent(before);
        AssertScope(entry);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RemovingSelectedPlan_PreservesReflectionOtherPlansAndPreviousSnapshot(int index)
    {
        var entry = Populated(); var before = Capture(entry);
        entry.RemovePlan(before.Plans[index].Id);
        var expected = before.Plans.Where((_, i) => i != index).ToArray();
        Assert.Equal(expected, entry.Plans);
        for (var i = 0; i < expected.Length; i++) Assert.Same(expected[i], entry.Plans[i]);
        Assert.Same(before.Reflection, entry.Reflection);
        AssertCapturedContent(before);
        AssertScope(entry);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EmptyOrMissingPlanReference_IsRejectedWithoutMutation(bool remove, bool empty)
    {
        var entry = Populated(); var before = Capture(entry);
        var id = empty ? Guid.Empty : Plan(99).Id;
        Action operation = () => { if (remove) entry.RemovePlan(id); else entry.EditPlan(id, "Changed"); };
        Exception error = empty ? Assert.Throws<ArgumentException>("planId", operation) : Assert.Throws<KeyNotFoundException>(operation);
        AssertPrivateError(error, before);
        AssertUnchanged(entry, before);
    }

    [Theory]
    [InlineData("null-title")]
    [InlineData("empty-title")]
    [InlineData("blank-title")]
    [InlineData("title-limit")]
    [InlineData("description-limit")]
    public void InvalidPlanEdit_PreservesCompleteAggregate(string defect)
    {
        var entry = Populated(); var before = Capture(entry);
        string? title = defect switch { "null-title" => null, "empty-title" => "", "blank-title" => " \t\r\n", "title-limit" => new string('x', 121), _ => "Changed title" };
        var description = defect == "description-limit" ? new string('x', 1_001) : "Changed description";
        var error = Assert.Throws<ArgumentException>(defect == "description-limit" ? "description" : "title",
            () => entry.EditPlan(entry.Plans[1].Id, title!, description));
        AssertPrivateError(error, before);
        AssertUnchanged(entry, before);
    }

    [Fact]
    public void FullReturnedSnapshot_CannotBypassLimitOverwriteRemoveOrClearPlans()
    {
        var entry = Populated(); for (var i = 4; i <= 20; i++) entry.AddPlan(Plan(i));
        var before = Capture(entry);
        var list = Assert.IsAssignableFrom<IList<DiaryPlan>>(entry.Plans);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(Plan(21)));
        AssertUnchanged(entry, before);
        Assert.Throws<NotSupportedException>(() => list[0] = Plan(21));
        AssertUnchanged(entry, before);
        Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        AssertUnchanged(entry, before);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        AssertUnchanged(entry, before);
    }

    [Fact]
    public void EditingReadContentValues_DoesNotImplicitlyReplaceAggregateContent()
    {
        var entry = Populated(); var before = Capture(entry);
        var reflection = entry.Reflection!.WithContent(null, "Detached replacement body");
        var plan = entry.Plans[0].WithContent("Detached replacement title");
        Assert.NotSame(entry.Reflection, reflection);
        Assert.NotSame(entry.Plans[0], plan);
        AssertUnchanged(entry, before);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public void DefaultPopulatedJson_ExposesOnlyPrivateMarkerAndDoesNotMutateState(string module)
    {
        var entry = Populated(module); var before = Capture(entry);
        var output = JsonSerializer.Serialize(entry);
        Assert.Equal("{\"IsPrivate\":true}", output);
        using var document = JsonDocument.Parse(output);
        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("IsPrivate", property.Name); Assert.True(property.Value.GetBoolean());
        Assert.DoesNotContain(Owner.ToString(), output);
        Assert.DoesNotContain(Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), output);
        Assert.DoesNotContain(module, output);
        Assert.DoesNotContain(ReflectionTitle, output); Assert.DoesNotContain(ReflectionBody, output);
        foreach (var plan in entry.Plans)
        {
            Assert.DoesNotContain(plan.Id.ToString(), output);
            Assert.DoesNotContain(plan.Title, output);
            Assert.DoesNotContain(plan.Description!, output);
        }
        AssertUnchanged(entry, before);
    }

    [Fact]
    public void DiagnosticsAndRejectedPrivateReplacement_DoNotExposeSensitiveValues()
    {
        var entry = Populated(); var before = Capture(entry);
        var error = Assert.Throws<ArgumentException>(() => entry.SetReflection("PRIVATE-REPLACEMENT" + new string('x', 101), "PRIVATE-REPLACEMENT-BODY"));
        AssertPrivateError(error, before);
        Assert.DoesNotContain("PRIVATE-REPLACEMENT", error.ToString());
        AssertPrivateError(new InvalidOperationException(entry.ToString()), before);
        AssertUnchanged(entry, before);
    }

    [Fact]
    public void PublicOperations_RemainExplicitManualDomainOperations()
    {
        Assert.Equal(new[] { "AddPlan", "EditPlan", "EditReflection", "RemovePlan", "RemoveReflection", "SetReflection", "ToString" },
            typeof(ModuleDiaryEntry).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
    }

    private static ModuleDiaryEntry Empty() => new(Owner, "learning", Day);
    private static ModuleDiaryEntry Populated(string module = "learning")
    {
        var entry = new ModuleDiaryEntry(Owner, module, Day);
        entry.SetReflection(ReflectionTitle, ReflectionBody);
        for (var i = 1; i <= 3; i++) entry.AddPlan(Plan(i));
        return entry;
    }
    private static DiaryPlan Plan(int number) => new(Guid.Parse($"00000000-0000-0000-0000-{number:D12}"), $"PRIVATE-PLAN-{number}", $"PRIVATE-DESCRIPTION-{number}");
    private sealed record State(Guid OwnerId, string Module, DateOnly Date, DiaryReflection? Reflection,
        IReadOnlyList<DiaryPlan> Plans, (Guid Id, string Title, string? Description)[] Values, string? Title, string? Body);
    private static State Capture(ModuleDiaryEntry entry) => new(entry.OwnerModuleProfileId, entry.Module, entry.Date,
        entry.Reflection, entry.Plans, entry.Plans.Select(plan => (plan.Id, plan.Title, plan.Description)).ToArray(), entry.Reflection?.Title, entry.Reflection?.Body);
    private static void AssertScope(ModuleDiaryEntry entry)
    {
        Assert.Equal(Owner, entry.OwnerModuleProfileId); Assert.Equal("learning", entry.Module);
        Assert.Equal(Day, entry.Date); Assert.True(entry.IsPrivate);
    }
    private static void AssertCapturedContent(State before)
    {
        Assert.Equal(before.Values, before.Plans.Select(plan => (plan.Id, plan.Title, plan.Description)).ToArray());
        Assert.Equal(before.Title, before.Reflection?.Title); Assert.Equal(before.Body, before.Reflection?.Body);
    }
    private static void AssertUnchanged(ModuleDiaryEntry entry, State before)
    {
        Assert.Equal(before.OwnerId, entry.OwnerModuleProfileId); Assert.Equal(before.Module, entry.Module);
        Assert.Equal(before.Date, entry.Date); Assert.True(entry.IsPrivate);
        Assert.Same(before.Reflection, entry.Reflection); Assert.Same(before.Plans, entry.Plans);
        AssertCapturedContent(before);
    }
    private static void AssertPrivateError(Exception error, State before)
    {
        var output = error.ToString();
        Assert.DoesNotContain(before.OwnerId.ToString(), output);
        if (before.Title is not null) Assert.DoesNotContain(before.Title, output);
        if (before.Body is not null) Assert.DoesNotContain(before.Body, output);
        foreach (var plan in before.Plans)
        {
            Assert.DoesNotContain(plan.Id.ToString(), output); Assert.DoesNotContain(plan.Title, output);
            if (plan.Description is not null) Assert.DoesNotContain(plan.Description, output);
        }
    }
}
