using System.Globalization;
using System.Text.Json;
using Andril.Common.Diary;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class DiaryPlanTests
{
    private static readonly Guid PlanId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string PrivateTitle = "  PRIVATE-PLAN-TITLE e\u0301 👩‍💻  ";
    private const string PrivateDescription = "  PRIVATE-PLAN-DESCRIPTION\r\nExact manual text  ";

    [Fact]
    public void ValidConstruction_PreservesStableIdentityAndExactPrivateContent()
    {
        AssertOriginal(Original());
    }

    [Fact]
    public void EmptyIdentity_IsRejectedWithoutDisclosingSuppliedContent()
    {
        var error = Assert.Throws<ArgumentException>("id", () => new DiaryPlan(Guid.Empty, PrivateTitle, PrivateDescription));
        Assert.DoesNotContain(PrivateTitle, error.ToString());
        Assert.DoesNotContain(PrivateDescription, error.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u2003")]
    public void MissingOrWhitespaceTitle_IsRejected(string? title)
    {
        Assert.Throws<ArgumentException>("title", () => new DiaryPlan(PlanId, title!, PrivateDescription));
    }

    [Theory]
    [InlineData("a", 120, true)]
    [InlineData("a", 121, false)]
    [InlineData("e\u0301", 120, true)]
    [InlineData("e\u0301", 121, false)]
    [InlineData("👩‍💻", 120, true)]
    [InlineData("👩‍💻", 121, false)]
    public void TitleBoundary_CountsTextElementsWithoutNormalizingContent(string unit, int count, bool accepted)
    {
        var title = string.Concat(Enumerable.Repeat(unit, count));
        Assert.Equal(count, new StringInfo(title).LengthInTextElements);
        if (accepted)
        {
            var plan = new DiaryPlan(PlanId, title, PrivateDescription);
            Assert.Equal(PlanId, plan.Id);
            Assert.Equal(title, plan.Title);
            Assert.Equal(PrivateDescription, plan.Description);
        }
        else Assert.Throws<ArgumentException>("title", () => new DiaryPlan(PlanId, title, PrivateDescription));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("  தமிழ் සිංහල e\u0301 👩‍💻\n  ")]
    public void OptionalDescription_IsAcceptedAndPreservedExactly(string? description)
    {
        var plan = new DiaryPlan(PlanId, PrivateTitle, description);
        Assert.Equal(PlanId, plan.Id);
        Assert.Equal(PrivateTitle, plan.Title);
        Assert.Equal(description, plan.Description);
    }

    [Theory]
    [InlineData("a", 1_000, true)]
    [InlineData("a", 1_001, false)]
    [InlineData("e\u0301", 1_000, true)]
    [InlineData("e\u0301", 1_001, false)]
    [InlineData("👩‍💻", 1_000, true)]
    [InlineData("👩‍💻", 1_001, false)]
    public void DescriptionBoundary_CountsTextElementsWithoutNormalizingContent(string unit, int count, bool accepted)
    {
        var description = string.Concat(Enumerable.Repeat(unit, count));
        Assert.Equal(count, new StringInfo(description).LengthInTextElements);
        if (accepted)
        {
            var plan = new DiaryPlan(PlanId, PrivateTitle, description);
            Assert.Equal(PlanId, plan.Id);
            Assert.Equal(PrivateTitle, plan.Title);
            Assert.Equal(description, plan.Description);
        }
        else Assert.Throws<ArgumentException>("description", () => new DiaryPlan(PlanId, PrivateTitle, description));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  Updated description\r\ne\u0301 👩‍💻  ")]
    public void ValidEdit_ReturnsExpectedNewContentWithSameIdentityAndPreservesOtherValues(string? description)
    {
        var original = Original();
        var otherId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var unrelated = new DiaryPlan(otherId, "Other title", "Other description");
        const string title = "  Updated title e\u0301 👩‍💻  ";
        var edited = original.WithContent(title, description);
        Assert.NotSame(original, edited);
        Assert.Equal(PlanId, edited.Id);
        Assert.Equal(title, edited.Title);
        Assert.Equal(description, edited.Description);
        AssertOriginal(original);
        Assert.Equal(otherId, unrelated.Id);
        Assert.Equal("Other title", unrelated.Title);
        Assert.Equal("Other description", unrelated.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u2003")]
    public void InvalidTitleEdit_PreservesCompleteOriginalIncludingIdentity(string? title)
    {
        var original = Original();
        Assert.Throws<ArgumentException>("title", () => original.WithContent(title!, "Changed description"));
        AssertOriginal(original);
    }

    [Theory]
    [InlineData("title", "a")]
    [InlineData("title", "e\u0301")]
    [InlineData("title", "👩‍💻")]
    [InlineData("description", "a")]
    [InlineData("description", "e\u0301")]
    [InlineData("description", "👩‍💻")]
    public void OversizedEdit_IsRejectedWithoutPartiallyApplyingOtherFieldOrIdentity(string field, string unit)
    {
        var original = Original();
        var unrelated = new DiaryPlan(PlanId, "Separate immutable title", "Separate immutable description");
        var title = field == "title" ? string.Concat(Enumerable.Repeat(unit, 121)) : "Changed title";
        var description = field == "description" ? string.Concat(Enumerable.Repeat(unit, 1_001)) : "Changed description";
        Assert.Throws<ArgumentException>(field, () => original.WithContent(title, description));
        AssertOriginal(original);
        Assert.Equal(PlanId, unrelated.Id);
        Assert.Equal("Separate immutable title", unrelated.Title);
        Assert.Equal("Separate immutable description", unrelated.Description);
    }

    [Fact]
    public void MaximumBoundaryEdit_AcceptsUnicodeLimitsAndRetainsIdentity()
    {
        var original = Original();
        var title = string.Concat(Enumerable.Repeat("e\u0301", 120));
        var description = string.Concat(Enumerable.Repeat("👩‍💻", 1_000));
        var edited = original.WithContent(title, description);
        Assert.Equal(PlanId, edited.Id);
        Assert.Equal(120, new StringInfo(edited.Title).LengthInTextElements);
        Assert.Equal(1_000, new StringInfo(edited.Description!).LengthInTextElements);
        Assert.Equal(title, edited.Title);
        Assert.Equal(description, edited.Description);
        AssertOriginal(original);
    }

    [Fact]
    public void OmittedDescription_IsOptionalDuringConstructionAndManualReplacement()
    {
        var draft = new DiaryPlan(PlanId, "Draft");
        Assert.Null(draft.Description);
        var original = Original();
        var edited = original.WithContent("New draft");
        Assert.Equal(PlanId, edited.Id);
        Assert.Equal("New draft", edited.Title);
        Assert.Null(edited.Description);
        AssertOriginal(original);
    }

    [Fact]
    public void DefaultJson_ExposesOnlyIdAndPreservesPopulatedPrivateSource()
    {
        var plan = Original();
        var output = JsonSerializer.Serialize(plan);
        using var json = JsonDocument.Parse(output);
        var property = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("Id", property.Name);
        Assert.Equal(PlanId, property.Value.GetGuid());
        Assert.DoesNotContain(PrivateTitle, output);
        Assert.DoesNotContain(PrivateDescription, output);
        AssertOriginal(plan);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    public void InvalidContentErrorsAndDiagnostics_DoNotRevealPrivateValues(string field)
    {
        var original = Original();
        var title = field == "title" ? PrivateTitle + new string('x', 121) : PrivateTitle;
        var description = field == "description" ? PrivateDescription + new string('x', 1_001) : PrivateDescription;
        var creation = Assert.Throws<ArgumentException>(field, () => new DiaryPlan(PlanId, title, description));
        var edit = Assert.Throws<ArgumentException>(field, () => original.WithContent(title, description));
        foreach (var output in new[] { creation.ToString(), edit.ToString(), original.ToString() })
        {
            Assert.DoesNotContain(PrivateTitle, output);
            Assert.DoesNotContain(PrivateDescription, output);
            Assert.DoesNotContain(title, output);
            Assert.DoesNotContain(description, output);
            Assert.DoesNotContain(PlanId.ToString(), output);
        }
        AssertOriginal(original);
    }

    [Fact]
    public void IdentityAndContent_HaveNoPublicMutationBypass()
    {
        Assert.True(typeof(DiaryPlan).IsSealed);
        Assert.Null(typeof(DiaryPlan).GetProperty(nameof(DiaryPlan.Id))!.SetMethod);
        Assert.All(typeof(DiaryPlan).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Empty(typeof(DiaryPlan).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    }

    private static DiaryPlan Original() => new(PlanId, PrivateTitle, PrivateDescription);

    private static void AssertOriginal(DiaryPlan plan)
    {
        Assert.Equal(PlanId, plan.Id);
        Assert.Equal(PrivateTitle, plan.Title);
        Assert.Equal(PrivateDescription, plan.Description);
    }
}
