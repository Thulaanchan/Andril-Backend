using System.Text;
using Andril.API.Contracts.Social;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

public sealed class SocialQuestionnaireSubmissionBodyParserTests
{
    private readonly SocialQuestionnaireSubmissionBodyParser _parser = new();

    [Fact]
    public void Parse_ValidFullSubmission_ReturnsSuccess()
    {
        const string json = """
        {
          "version": "v1",
          "skip": false,
          "answers": [
            { "questionId": "q01", "optionId": "5" },
            { "questionId": "q02", "optionId": "3" }
          ]
        }
        """;

        var result = _parser.Parse(json);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.NotNull(result.Submission);
        Assert.Equal("v1", result.Submission.Version);
        Assert.False(result.Submission.Skip);
        Assert.Equal(2, result.Submission.Answers.Count);
        Assert.Equal("q01", result.Submission.Answers[0].QuestionId);
        Assert.Equal("5", result.Submission.Answers[0].OptionId);
        Assert.Equal("q02", result.Submission.Answers[1].QuestionId);
        Assert.Equal("3", result.Submission.Answers[1].OptionId);
    }

    [Fact]
    public void Parse_ValidSkipSubmission_ReturnsSuccess()
    {
        const string json = """
        {
          "version": "v1",
          "skip": true,
          "answers": []
        }
        """;

        var result = _parser.Parse(json);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.NotNull(result.Submission);
        Assert.Equal("v1", result.Submission.Version);
        Assert.True(result.Submission.Skip);
        Assert.Empty(result.Submission.Answers);
    }

    [Fact]
    public void Parse_DuplicateJsonKeyInRoot_FailsWithDiagnostic()
    {
        const string json = """
        {
          "version": "v1",
          "version": "v2",
          "answers": []
        }
        """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
        Assert.Contains("Duplicate property 'version'", result.Error);
    }

    [Fact]
    public void Parse_DuplicateJsonKeyInAnswerObject_FailsWithDiagnostic()
    {
        const string json = """
        {
          "version": "v1",
          "answers": [
            {
              "questionId": "q01",
              "questionId": "q02",
              "optionId": "4"
            }
          ]
        }
        """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
        Assert.Contains("Duplicate property 'questionId'", result.Error);
    }

    [Fact]
    public void Parse_UnknownRootProperty_FailsWithDiagnostic()
    {
        const string json = """
        {
          "version": "v1",
          "roleOverride": "SuperAdmin",
          "answers": []
        }
        """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
        Assert.Contains("Unknown property 'roleOverride'", result.Error);
    }

    [Fact]
    public void Parse_UnknownAnswerProperty_FailsWithDiagnostic()
    {
        const string json = """
        {
          "version": "v1",
          "answers": [
            {
              "questionId": "q01",
              "optionId": "3",
              "tamperedScore": 100
            }
          ]
        }
        """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
        Assert.Contains("Unknown property 'tamperedScore'", result.Error);
    }

    [Fact]
    public void Parse_DisallowedPrototypePollutionProperty_FailsWithDiagnostic()
    {
        const string json = """
        {
          "version": "v1",
          "__proto__": { "polluted": true },
          "answers": []
        }
        """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
        Assert.Contains("Disallowed property '__proto__'", result.Error);
    }

    [Fact]
    public void Parse_DisallowedConstructorPropertyInAnswer_FailsWithDiagnostic()
    {
        const string json = """
        {
          "version": "v1",
          "answers": [
            {
              "questionId": "q01",
              "optionId": "1",
              "constructor": "malicious"
            }
          ]
        }
        """;

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
        Assert.Contains("Disallowed property 'constructor'", result.Error);
    }

    [Fact]
    public void Parse_OversizedPayload_FailsWithDiagnostic()
    {
        // 35 KB padding in an answer array
        var largeString = new string('A', 35 * 1024);
        var json = $"{{\"version\": \"v1\", \"padding\": \"{largeString}\"}}";

        var result = _parser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
        Assert.Contains("exceeds maximum limit", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_FailsSafely(string? input)
    {
        var result = _parser.Parse(input);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
        Assert.Contains("Payload cannot be null or empty", result.Error);
    }

    [Fact]
    public void Parse_MalformedJson_FailsSafelyWithoutSecretLeakage()
    {
        const string malformed = "{ \"version\": \"v1\", \"answers\": [ { missing_quotes } ] }";

        var result = _parser.Parse(malformed);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
        Assert.Contains("Malformed JSON syntax", result.Error);
    }

    [Fact]
    public void Parse_TrailingContentAfterRootObject_FailsSafely()
    {
        const string trailing = "{\"version\":\"v1\",\"skip\":true,\"answers\":[]}{\"extra\":1}";

        var result = _parser.Parse(trailing);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Submission);
        Assert.NotNull(result.Error);
    }
}
