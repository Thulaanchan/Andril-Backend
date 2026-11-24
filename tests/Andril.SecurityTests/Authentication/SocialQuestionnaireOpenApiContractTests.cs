using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class SocialQuestionnaireOpenApiContractTests
{
    private const string ContractRelativePath = "contracts/openapi/andril-api.v1.yaml";

    [Fact]
    public void OpenApiContract_ContainsAuthorizedQuestionnaireGetEndpoint()
    {
        var rootDir = GetRepositoryRoot();
        var yamlPath = Path.Combine(rootDir, ContractRelativePath);
        Assert.True(File.Exists(yamlPath), $"OpenAPI contract file not found at {yamlPath}");

        var yaml = File.ReadAllText(yamlPath);

        // Path and method verification
        Assert.Contains("  /api/v1/social/questionnaires/active:", yaml);
        Assert.Contains("      operationId: getActiveSocialQuestionnaire", yaml);
        Assert.Contains("$ref: '#/components/schemas/QuestionnaireDefinitionDto'", yaml);
        Assert.Contains("Cache-Control:", yaml);
        Assert.Contains("no-store", yaml);

        // Schema structure verification
        Assert.Contains("    QuestionnaireDefinitionDto:", yaml);
        Assert.Contains("    QuestionnaireQuestionDto:", yaml);
        Assert.Contains("    QuestionnaireOptionDto:", yaml);

        // Verify Questionnaire schemas do not expose private profile IDs or match scores
        var questionnaireSchema = yaml.Split("    QuestionnaireDefinitionDto:")[1].Split("    OnboardingStatus:")[0];
        Assert.DoesNotContain("profileId", questionnaireSchema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountId", questionnaireSchema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("score", questionnaireSchema, StringComparison.OrdinalIgnoreCase);

        // Negative check: verify no unintended questionnaire POST/PUT/DELETE operations
        Assert.DoesNotContain("post:\r\n      operationId: submitSocialQuestionnaire", yaml);
        Assert.DoesNotContain("post:\n      operationId: submitSocialQuestionnaire", yaml);
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Andril.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }
}
