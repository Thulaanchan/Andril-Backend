using System.Reflection;
using System.Text.Json;
using Andril.Learning.CV;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class CvSectionTests
{
    [Theory]
    [InlineData(CvSectionCategory.PersonalInformation)]
    [InlineData(CvSectionCategory.ProfessionalSummary)]
    [InlineData(CvSectionCategory.Education)]
    [InlineData(CvSectionCategory.WorkExperience)]
    [InlineData(CvSectionCategory.Skills)]
    [InlineData(CvSectionCategory.Projects)]
    [InlineData(CvSectionCategory.CertificationsAndAchievements)]
    public void ApprovedCategory_SupportsOneLogicalSectionWithMultipleIndependentEntries(CvSectionCategory category)
    {
        var id = Guid.NewGuid(); var first = new CvSection.Entry(Guid.NewGuid(), id, "First");
        var second = new CvSection.Entry(Guid.NewGuid(), id, "Second");
        var section = new CvSection(id, category, [first, second]);
        Assert.Equal(id, section.Id); Assert.Equal(category, section.Category);
        Assert.Equal(new[] { first, second }, section.Entries); Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(int.MaxValue)]
    public void UnsupportedCategory_IsRejected(int value)
    {
        Assert.Throws<ArgumentException>("category", () => new CvSection(Guid.NewGuid(), (CvSectionCategory)value, []));
    }

    [Fact]
    public void SectionIdentityCannotBeEmpty()
    {
        Assert.Throws<ArgumentException>("id", () => new CvSection(Guid.Empty, CvSectionCategory.Skills, []));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EntryRequiresNonemptyEntryAndSectionIdentities(bool emptyEntry)
    {
        Assert.Throws<ArgumentException>(() => new CvSection.Entry(emptyEntry ? Guid.Empty : Guid.NewGuid(),
            emptyEntry ? Guid.NewGuid() : Guid.Empty, "Private"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("  தமிழ் සිංහල e\u0301 👩🏽‍💻  ")]
    public void IncompleteDraftAndExactManualText_AreAllowedWithoutInventedFieldRules(string? text)
    {
        var sectionId = Guid.NewGuid(); var entry = new CvSection.Entry(Guid.NewGuid(), sectionId, text);
        var section = new CvSection(sectionId, CvSectionCategory.Education, [entry]);
        Assert.Equal(text, Assert.Single(section.Entries).Text);
        Assert.Empty(new CvSection(Guid.NewGuid(), CvSectionCategory.PersonalInformation, []).Entries);
    }

    [Theory]
    [InlineData("null-collection")]
    [InlineData("null-entry")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    public void InvalidReplacement_PreservesOriginalIdentityCategoryAndEntries(string defect)
    {
        var id = Guid.NewGuid(); var entry = new CvSection.Entry(Guid.NewGuid(), id, "Keep private");
        var section = new CvSection(id, CvSectionCategory.Projects, [entry]); var snapshot = section.Entries;
        if (defect == "null-collection") Assert.Throws<ArgumentNullException>(() => section.WithEntries(null!));
        else
        {
            var values = defect switch
            {
                "null-entry" => new CvSection.Entry[] { null! },
                "duplicate" => new[] { entry, new CvSection.Entry(entry.Id, id, "Changed") },
                _ => new[] { new CvSection.Entry(Guid.NewGuid(), Guid.NewGuid(), "Foreign") }
            };
            Assert.Throws<ArgumentException>(() => section.WithEntries(values));
        }
        Assert.Equal(id, section.Id); Assert.Equal(CvSectionCategory.Projects, section.Category);
        Assert.Same(snapshot, section.Entries); Assert.Same(entry, Assert.Single(section.Entries));
        Assert.Equal("Keep private", entry.Text);
    }

    [Fact]
    public void AcceptedReplacement_IsAnImmutableSnapshot_WithStableLogicalIdentity()
    {
        var id = Guid.NewGuid(); var entry = new CvSection.Entry(Guid.NewGuid(), id, "Before");
        var section = new CvSection(id, CvSectionCategory.WorkExperience, [entry]);
        var changed = new CvSection.Entry(entry.Id, id, "After"); var replacement = section.WithEntries([changed]);
        Assert.Equal(id, replacement.Id); Assert.Equal(section.Category, replacement.Category);
        Assert.Same(changed, Assert.Single(replacement.Entries)); Assert.Equal("Before", Assert.Single(section.Entries).Text);
    }

    [Fact]
    public void SuppliedCollectionsAreCopied_AndReturnedCollectionsCannotMutateState()
    {
        var id = Guid.NewGuid(); var entry = new CvSection.Entry(Guid.NewGuid(), id, "Keep");
        var input = new List<CvSection.Entry> { entry }; var section = new CvSection(id, CvSectionCategory.Skills, input);
        input.Clear(); Assert.Same(entry, Assert.Single(section.Entries));
        var list = (IList<CvSection.Entry>)section.Entries;
        Assert.Throws<NotSupportedException>(() => list.Add(entry));
        Assert.Throws<NotSupportedException>(() => list[0] = entry);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    [Fact]
    public void DefaultSectionAndEntryJsonAndDiagnostics_DoNotExposePotentialContactText()
    {
        var id = Guid.NewGuid(); const string text = "PRIVATE-CONTACT-MARKER private@example.test +000-private";
        var entry = new CvSection.Entry(Guid.NewGuid(), id, text);
        var section = new CvSection(id, CvSectionCategory.PersonalInformation, [entry]);
        var outputs = new[] { JsonSerializer.Serialize(section), JsonSerializer.Serialize(section.Entries),
            JsonSerializer.Serialize(entry), section.ToString(), entry.ToString() };
        Assert.All(outputs, output => Assert.DoesNotContain(text, output));
        Assert.Equal(text, entry.Text); // Still available to the future authorized private editor, never render-ready.
        var error = Assert.Throws<ArgumentException>(() => section.WithEntries([entry, entry]));
        Assert.DoesNotContain(text, error.ToString());
    }

    [Fact]
    public void LogicalCategoriesAndPrivateEditableEntrySurface_AreImmutable()
    {
        Assert.Equal(7, Enum.GetValues<CvSectionCategory>().Length);
        foreach (var type in new[] { typeof(CvSection), typeof(CvSection.Entry) })
        {
            Assert.True(type.IsSealed); Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        }
        Assert.Equal(new[] { "ToString", "WithEntries" }, typeof(CvSection)
            .GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
    }
}
