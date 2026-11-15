
using Andril.Social.Personality;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class QuestionnaireDefinitionCatalogTests
{
    private static QuestionnaireDefinition CreateDefinition(
        string version = "test-v1")
    {
        return new QuestionnaireDefinition(
            version,
            new[]
            {
                new QuestionDefinition(
                    "q1",
                    "Dummy personality question",
                    true,
                    new[]
                    {
                        new QuestionOption("yes", "Yes"),
                        new QuestionOption("no", "No")
                    })
            });
    }

    [Fact]
    public void ValidDefinition_CanBeRetrieved()
    {
        var catalog = new QuestionnaireDefinitionCatalog(
            new[] { CreateDefinition() });

        var result = catalog.FindByVersion("test-v1");

        Assert.NotNull(result);
        Assert.Equal("test-v1", result.Version);
    }

    [Fact]
    public void UnknownVersion_ReturnsNull()
    {
        var catalog = new QuestionnaireDefinitionCatalog(
            new[] { CreateDefinition() });

        Assert.Null(catalog.FindByVersion("unknown"));
    }

    [Fact]
    public void NullOrBlankVersion_ReturnsNull()
    {
        var catalog = new QuestionnaireDefinitionCatalog(
            new[] { CreateDefinition() });

        Assert.Null(catalog.FindByVersion(null));
        Assert.Null(catalog.FindByVersion(""));
        Assert.Null(catalog.FindByVersion(" "));
    }

    [Fact]
    public void InvalidDefinition_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new QuestionnaireDefinitionCatalog(
                new[]
                {
                    new QuestionnaireDefinition(
                        "",
                        Array.Empty<QuestionDefinition>())
                }));
    }

    [Fact]
    public void DuplicateVersions_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new QuestionnaireDefinitionCatalog(
                new[]
                {
                    CreateDefinition("test-v1"),
                    CreateDefinition("test-v1")
                }));
    }

    [Fact]
    public void NullDefinitionsCollection_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new QuestionnaireDefinitionCatalog(null!));
    }
}
