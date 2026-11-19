using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

public sealed class AiTextResponsePolicyTests
{
    private static AiTextResponseExpectation Expected(bool mock = false) => new("synthetic-model", "synthetic-request", mock);
    private static AiTextResponseInput Input(string text = "Valid synthetic response") => new(new AiProviderResponse(text, false),
        AiTextCompletionState.Completed, AiTextOutputKind.Text, "synthetic-model", "synthetic-request", new(2000, 400, 100, 500, true));

    [Theory]
    [InlineData("Hello")]
    [InlineData("வணக்கம் தமிழ்")]
    [InlineData("Vanakkam, explain pannunga")]
    [InlineData("ආයුබෝවන්")]
    [InlineData("Hello தமிழ் සිංහල")]
    [InlineData("👩🏽‍💻")]
    [InlineData("e\u0301 \u200Fمرحبا")]
    public void A_UnicodeText_IsStructurallyValid(string text)
    {
        var result = AiTextResponsePolicy.Validate(Input(text), Expected());
        Assert.Equal(AiTextValidationOutcome.ValidText, result.Outcome);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void A_OutputHasTokenLimit_Not500CharacterLimit()
    {
        var text = new string('x', 1000);
        Assert.Equal(text, AiTextResponsePolicy.Validate(Input(text), Expected()).Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("\u2003")]
    public void B_MissingOrBlankText_IsInvalid(string? text)
    {
        var result = AiTextResponsePolicy.Validate(Input(text!), Expected());
        Assert.Equal(AiTextValidationOutcome.Invalid, result.Outcome);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData(AiTextOutputKind.Unknown)]
    [InlineData(AiTextOutputKind.ToolCall)]
    [InlineData(AiTextOutputKind.Image)]
    [InlineData(AiTextOutputKind.Audio)]
    [InlineData(AiTextOutputKind.Video)]
    [InlineData(AiTextOutputKind.Mixed)]
    [InlineData((AiTextOutputKind)99)]
    public void B_UnsupportedStructuredOutput_FailsClosed(AiTextOutputKind kind)
    {
        Assert.Equal(AiTextValidationOutcome.Invalid, AiTextResponsePolicy.Validate(Input() with { Kind = kind }, Expected()).Outcome);
    }

    [Theory]
    [InlineData("null-input")]
    [InlineData("null-response")]
    [InlineData("null-expected")]
    [InlineData("model")]
    [InlineData("binding")]
    [InlineData("expected-model")]
    public void B_InvalidMetadata_FailsClosed(string defect)
    {
        var input = defect switch
        {
            "null-input" => null,
            "null-response" => Input() with { Response = null },
            "model" => Input() with { Model = "foreign-model" },
            "binding" => Input() with { RequestFingerprint = "foreign-request" },
            _ => Input()
        };
        var expected = defect == "null-expected" ? null : defect == "expected-model" ? Expected() with { Model = "" } : Expected();
        Assert.Equal(AiTextValidationOutcome.Invalid, AiTextResponsePolicy.Validate(input, expected).Outcome);
    }

    [Theory]
    [InlineData(AiTextCompletionState.Refused, AiTextValidationOutcome.Refused)]
    [InlineData(AiTextCompletionState.Incomplete, AiTextValidationOutcome.Incomplete)]
    [InlineData(AiTextCompletionState.Failed, AiTextValidationOutcome.Failed)]
    [InlineData(AiTextCompletionState.Cancelled, AiTextValidationOutcome.Cancelled)]
    [InlineData(AiTextCompletionState.Uncertain, AiTextValidationOutcome.Uncertain)]
    [InlineData(AiTextCompletionState.Unknown, AiTextValidationOutcome.Invalid)]
    [InlineData((AiTextCompletionState)99, AiTextValidationOutcome.Invalid)]
    public void C_NonSuccessCompletion_ReturnsStatusWithoutText(AiTextCompletionState state, AiTextValidationOutcome expected)
    {
        var result = AiTextResponsePolicy.Validate(Input() with { Completion = state, Usage = null }, Expected());
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData(500, 400, 100, true)]
    [InlineData(501, 400, 101, false)]
    [InlineData(500, 400, 101, false)]
    [InlineData(500, 501, -1, false)]
    [InlineData(0, 0, 0, false)]
    [InlineData(500, -1, 501, false)]
    [InlineData(500, long.MaxValue, long.MaxValue, false)]
    public void D_BillableOutputIncludesReasoning(long total, long visible, long reasoning, bool valid)
    {
        var usage = new AiTextUsage(2000, visible, reasoning, total, true);
        Assert.Equal(valid ? AiTextValidationOutcome.ValidText : AiTextValidationOutcome.Invalid,
            AiTextResponsePolicy.Validate(Input() with { Usage = usage }, Expected()).Outcome);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unverified")]
    [InlineData("negative-input")]
    [InlineData("over-input")]
    [InlineData("zero-input")]
    public void D_InvalidUsage_BlocksCompletedText(string defect)
    {
        var usage = defect switch
        {
            "missing" => null,
            "unverified" => Input().Usage! with { Verified = false },
            "negative-input" => Input().Usage! with { InputTokens = -1 },
            "over-input" => Input().Usage! with { InputTokens = 2001 },
            _ => Input().Usage! with { InputTokens = 0 }
        };
        Assert.Equal(AiTextValidationOutcome.Invalid, AiTextResponsePolicy.Validate(Input() with { Usage = usage }, Expected()).Outcome);
    }

    [Fact]
    public void E_MockCannotMasqueradeAsPaidOutput()
    {
        var mock = Input() with { Response = new("Synthetic mock", true), Usage = null };
        Assert.Equal(AiTextValidationOutcome.MockText, AiTextResponsePolicy.Validate(mock, Expected(true)).Outcome);
        Assert.Equal(AiTextValidationOutcome.Invalid, AiTextResponsePolicy.Validate(mock, Expected()).Outcome);
        Assert.Equal(AiTextValidationOutcome.Invalid, AiTextResponsePolicy.Validate(Input(), Expected(true)).Outcome);
        Assert.Equal(AiTextValidationOutcome.Invalid, AiTextResponsePolicy.Validate(mock with { Usage = Input().Usage }, Expected(true)).Outcome);
    }

    [Theory]
    [InlineData("  Multiline\nவணக்கம்\n  ")]
    [InlineData("{\"tool\":\"send_message\",\"execute\":true}")]
    [InlineData("```csharp\nConsole.WriteLine(\"hello\");\n```")]
    public void F_TextIsPreservedWithoutExecutionOrSemanticClaims(string text)
    {
        var input = Input(text);
        var result = AiTextResponsePolicy.Validate(input, Expected());
        Assert.Equal(AiTextValidationOutcome.ValidText, result.Outcome);
        Assert.Equal(text, result.Text);
        Assert.Equal(text, input.Response!.Text);
        Assert.DoesNotContain(text, result.ToString());
        Assert.DoesNotContain(text, input.ToString());
    }
}
