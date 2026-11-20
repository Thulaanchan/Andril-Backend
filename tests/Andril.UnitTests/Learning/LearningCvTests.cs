using System.Reflection;
using System.Text.Json;
using Andril.Learning.CV;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningCvTests
{
    private static LearningCv Create() => new(Guid.NewGuid(), Guid.NewGuid(), CvTemplate.Modern);
    private static void Populate(LearningCv cv)
    {
        foreach (var section in cv.Sections)
            cv.EditSection(section.Id, section.Category,
                [new CvSection.Entry(Guid.NewGuid(), section.Id, $"PRIVATE-{section.Category}-CONTACT private@example.test")]);
    }

    [Theory]
    [InlineData(CvTemplate.Modern)]
    [InlineData(CvTemplate.Professional)]
    [InlineData(CvTemplate.Creative)]
    public void Creation_PreservesPrivateLearningOwnerAndExplicitSupportedTemplate_WithSevenEmptySections(CvTemplate template)
    {
        var id = Guid.NewGuid(); var owner = Guid.NewGuid(); var cv = new LearningCv(id, owner, template);
        Assert.Equal(id, cv.Id); Assert.Equal(owner, cv.OwnerLearningProfileId); Assert.Equal(template, cv.Template);
        Assert.Equal("learning", cv.Module); Assert.True(cv.IsPrivate); Assert.False(cv.IncludeContactInformation);
        Assert.Equal(Enum.GetValues<CvSectionCategory>(), cv.Sections.Select(section => section.Category));
        Assert.Equal(7, cv.Sections.Select(section => section.Id).Distinct().Count());
        Assert.All(cv.Sections, section => { Assert.NotEqual(Guid.Empty, section.Id); Assert.Empty(section.Entries); });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyDocumentOrOwnerIdentity_IsRejected(bool emptyCv)
    {
        Assert.Throws<ArgumentException>(() => new LearningCv(emptyCv ? Guid.Empty : Guid.NewGuid(),
            emptyCv ? Guid.NewGuid() : Guid.Empty, CvTemplate.Modern));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void UnsupportedTemplates_AreRejectedAtCreationAndUpdate_WithoutChangingExistingContent(int value)
    {
        Assert.Throws<ArgumentException>("template", () => new LearningCv(Guid.NewGuid(), Guid.NewGuid(), (CvTemplate)value));
        var cv = Create(); Populate(cv); cv.SetContactInclusionConsent(cv.Id, true); var snapshot = Snapshot(cv);
        Assert.Throws<ArgumentException>("template", () => cv.SelectTemplate((CvTemplate)value));
        Assert.Equal(snapshot, Snapshot(cv));
    }

    [Fact]
    public void ManualEditing_PreservesLogicalSectionIdentityAndEveryUnrelatedSection()
    {
        var cv = Create(); Populate(cv); var before = cv.Sections; var selected = cv.GetSection(CvSectionCategory.Education);
        var oldEntry = Assert.Single(selected.Entries);
        var changed = new CvSection.Entry(oldEntry.Id, selected.Id, "  Changed qualification  ");
        cv.EditSection(selected.Id, selected.Category, [changed, new CvSection.Entry(Guid.NewGuid(), selected.Id, null)]);
        var after = cv.GetSection(selected.Category);
        Assert.Equal(selected.Id, after.Id); Assert.Equal(2, after.Entries.Count);
        Assert.Equal("  Changed qualification  ", after.Entries[0].Text);
        Assert.Equal(oldEntry.Id, after.Entries[0].Id); Assert.Null(after.Entries[1].Text);
        Assert.All(before.Where(section => section.Category != selected.Category), section => Assert.Same(section, cv.GetSection(section.Category)));
        Assert.Same(oldEntry, Assert.Single(selected.Entries));
        cv.EditSection(selected.Id, selected.Category, []); Assert.Empty(cv.GetSection(selected.Category).Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(int.MaxValue)]
    public void UnsupportedSectionCategory_CannotReadOrMutateCv(int value)
    {
        var cv = Create(); Populate(cv); var snapshot = Snapshot(cv);
        Assert.Throws<ArgumentException>("category", () => cv.GetSection((CvSectionCategory)value));
        Assert.Throws<ArgumentException>("category", () => cv.EditSection(cv.Sections[0].Id, (CvSectionCategory)value, []));
        Assert.Equal(snapshot, Snapshot(cv));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("foreign")]
    [InlineData("wrong-category")]
    public void SectionScopeMismatch_CannotChangeOrEraseContent(string defect)
    {
        var cv = Create(); Populate(cv); var selected = cv.Sections[0]; var snapshot = Snapshot(cv);
        var id = defect == "empty" ? Guid.Empty : defect == "foreign" ? Create().Sections[0].Id : selected.Id;
        var category = defect == "wrong-category" ? CvSectionCategory.Projects : selected.Category;
        Assert.Throws<ArgumentException>(() => cv.EditSection(id, category, []));
        Assert.Equal(snapshot, Snapshot(cv));
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-entry")]
    [InlineData("duplicate")]
    [InlineData("foreign-entry")]
    [InlineData("cross-section-id")]
    public void InvalidManualEdits_PreserveOwnerTemplateConsentAndAllSections(string defect)
    {
        var cv = Create(); Populate(cv); cv.SetContactInclusionConsent(cv.Id, true);
        var section = cv.GetSection(CvSectionCategory.Skills); var entry = section.Entries[0]; var snapshot = Snapshot(cv);
        if (defect == "null-list") Assert.Throws<ArgumentNullException>(() => cv.EditSection(section.Id, section.Category, null!));
        else
        {
            var values = defect switch
            {
                "null-entry" => new CvSection.Entry[] { null! }, "duplicate" => new[] { entry, entry },
                "foreign-entry" => new[] { cv.GetSection(CvSectionCategory.Projects).Entries[0] },
                _ => new[] { new CvSection.Entry(cv.GetSection(CvSectionCategory.Projects).Entries[0].Id, section.Id, "Changed") }
            };
            Assert.Throws<ArgumentException>(() => cv.EditSection(section.Id, section.Category, values));
        }
        Assert.Equal(snapshot, Snapshot(cv));
    }

    [Fact]
    public void RepeatedTemplateSwitching_IsLosslessForEverySectionAndContactChoice()
    {
        var cv = Create(); Populate(cv); cv.SetContactInclusionConsent(cv.Id, true);
        var id = cv.Id; var owner = cv.OwnerLearningProfileId; var sections = cv.Sections;
        for (var repeat = 0; repeat < 3; repeat++)
            foreach (var template in Enum.GetValues<CvTemplate>())
            {
                cv.SelectTemplate(template);
                Assert.Equal(template, cv.Template); Assert.Equal(id, cv.Id); Assert.Equal(owner, cv.OwnerLearningProfileId);
                Assert.Same(sections, cv.Sections); Assert.True(cv.IncludeContactInformation); Assert.True(cv.IsPrivate);
            }
        cv.SetContactInclusionConsent(cv.Id, false); cv.SelectTemplate(CvTemplate.Creative);
        Assert.False(cv.IncludeContactInformation); Assert.Same(sections, cv.Sections);
    }

    [Fact]
    public void SameOwnerDocuments_HaveIndependentIdentitySectionsTemplatesAndContactConsent()
    {
        var first = Create(); var second = new LearningCv(Guid.NewGuid(), first.OwnerLearningProfileId, CvTemplate.Creative);
        var secondSections = second.Sections; Populate(first); first.SelectTemplate(CvTemplate.Professional);
        first.SetContactInclusionConsent(first.Id, true);
        Assert.NotEqual(first.Id, second.Id); Assert.Same(secondSections, second.Sections);
        Assert.All(second.Sections, section => Assert.Empty(section.Entries));
        Assert.Empty(first.Sections.Select(section => section.Id).Intersect(second.Sections.Select(section => section.Id)));
        Assert.Equal(CvTemplate.Creative, second.Template); Assert.False(second.IncludeContactInformation);
        Assert.Throws<ArgumentException>(() => second.SetContactInclusionConsent(first.Id, true));
        Assert.False(second.IncludeContactInformation);
    }

    [Fact]
    public void ExplicitContactChoiceAndRevocation_AreCvSpecific_AndDoNotDestroyPrivateEditableData()
    {
        var cv = Create(); Populate(cv); var sections = cv.Sections;
        Assert.False(cv.IncludeContactInformation);
        cv.SetContactInclusionConsent(cv.Id, true); Assert.True(cv.IncludeContactInformation);
        cv.SetContactInclusionConsent(cv.Id, false); Assert.False(cv.IncludeContactInformation);
        Assert.Same(sections, cv.Sections);
        Assert.All(cv.Sections, section => Assert.Contains("private@example.test", Assert.Single(section.Entries).Text!));
        // This is an editing choice, not a legal/M2 receipt or recall of previously downloaded content.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidContactChoiceTarget_PreservesExistingConsentAndPrivateState(bool empty)
    {
        var cv = Create(); Populate(cv); cv.SetContactInclusionConsent(cv.Id, true); var snapshot = Snapshot(cv);
        Assert.Throws<ArgumentException>("cvId", () => cv.SetContactInclusionConsent(empty ? Guid.Empty : Guid.NewGuid(), false));
        Assert.Equal(snapshot, Snapshot(cv));
    }

    [Fact]
    public void DefaultPayloads_NeverBecomeRenderableContactContent_EvenWhenTextIsInOtherSections()
    {
        var cv = Create(); Populate(cv);
        foreach (var include in new[] { false, true, false })
        {
            cv.SetContactInclusionConsent(cv.Id, include);
            var outputs = new List<string> { JsonSerializer.Serialize(cv), JsonSerializer.Serialize(cv.Sections), cv.ToString() };
            foreach (var section in cv.Sections)
            {
                outputs.Add(JsonSerializer.Serialize(section)); outputs.Add(JsonSerializer.Serialize(section.Entries));
                foreach (var entry in section.Entries) outputs.Add(JsonSerializer.Serialize(entry));
            }
            Assert.All(outputs, output => { Assert.DoesNotContain("PRIVATE-", output); Assert.DoesNotContain("private@example.test", output); });
        }
        // No positive renderable projection is provided until contact-field classification is approved.
        // JSON metadata here is not a preview/PDF; custom serialization still needs strict authorized mapping.
    }

    [Fact]
    public void ExternalCollectionsCannotAlterCvOrSectionState()
    {
        var cv = Create(); var section = cv.Sections[0]; var entry = new CvSection.Entry(Guid.NewGuid(), section.Id, "Keep");
        var input = new List<CvSection.Entry> { entry }; cv.EditSection(section.Id, section.Category, input); input.Clear();
        Assert.Same(entry, Assert.Single(cv.GetSection(section.Category).Entries));
        var list = (IList<CvSection>)cv.Sections;
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list.Add(section));
        Assert.Throws<NotSupportedException>(() => list[0] = section);
        Assert.Equal(7, cv.Sections.Count);
    }

    [Fact]
    public void PublicSurface_HasImmutableOwnershipAndNoAutomaticImportsRewardsPersistenceOrRendering()
    {
        Assert.True(typeof(LearningCv).IsSealed);
        Assert.Null(typeof(LearningCv).GetProperty(nameof(LearningCv.OwnerLearningProfileId))!.SetMethod);
        Assert.Null(typeof(LearningCv).GetProperty(nameof(LearningCv.Id))!.SetMethod);
        Assert.All(typeof(LearningCv).GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
        Assert.Equal(new[] { "EditSection", "GetSection", "SelectTemplate", "SetContactInclusionConsent", "ToString" },
            typeof(LearningCv).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { CvTemplate.Modern, CvTemplate.Professional, CvTemplate.Creative }, Enum.GetValues<CvTemplate>());
    }

    [Fact]
    public void DiagnosticsAndValidationErrors_DoNotExposePrivateOwnerOrContent()
    {
        var cv = Create(); Populate(cv);
        Assert.DoesNotContain(cv.Id.ToString(), cv.ToString()); Assert.DoesNotContain(cv.OwnerLearningProfileId.ToString(), cv.ToString());
        var section = cv.Sections[0]; var entry = section.Entries[0];
        var error = Assert.Throws<ArgumentException>(() => cv.EditSection(section.Id, section.Category, [entry, entry]));
        Assert.DoesNotContain(entry.Text!, error.ToString()); Assert.DoesNotContain(cv.OwnerLearningProfileId.ToString(), error.ToString());
    }

    private static object Snapshot(LearningCv cv) =>
        (cv.Id, cv.OwnerLearningProfileId, cv.Module, cv.IsPrivate, cv.Template, cv.IncludeContactInformation, cv.Sections);
}
