using Andril.API.Integrations.AI;
using Andril.AI.Policies;
using Andril.AI.Providers.OpenAi;
using Xunit;

namespace Andril.SecurityTests.AI;

public sealed class AiOptionsTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private static Guid[] Accounts() => Enumerable.Range(1, 6).Select(Id).ToArray();
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static AiBudgetWindow Window() => new(Id(20), Start, Start.AddDays(14));
    private static bool Valid(AiOptions options) => new AiOptionsValidator().Validate(null, options).Succeeded;

    [Fact]
    public void DisabledDefaultsAreValidWithoutInventingOperationalConfiguration()
    {
        var o = new AiOptions();
        Assert.False(o.LiveExecutionEnabled); Assert.True(Valid(o));
        Assert.Equal(OpenAiResponsesRequestSerializer.ApprovedModel, o.Model);
        Assert.Equal(AiPrototypeLimits.MaxInputTokens, o.MaxInputTokens);
        Assert.Equal(AiPrototypeLimits.MaxOutputTokens, o.MaxBillableOutputTokens);
        Assert.Null(o.ProviderTimeout); Assert.Null(o.PrototypeBudgetWindow); Assert.Empty(o.PrototypeAccounts!);
        Assert.True(o.RequiresTrustedCredential);
        Assert.True(new AiOptionsValidator().Validate(null, null!).Failed);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("unsupported")]
    public void MissingOrUnsupportedModelCannotFallBack(string? model) => Assert.False(Valid(new() { Model = model! }));

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1999)] [InlineData(2000)] [InlineData(2001)]
    public void InputCeilingIsExactlyPrototypeLimit(int value) =>
        Assert.Equal(value == AiPrototypeLimits.MaxInputTokens, Valid(new() { MaxInputTokens = value }));

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(499)] [InlineData(500)] [InlineData(501)]
    public void OutputCeilingIsExactlyPrototypeLimit(int value) =>
        Assert.Equal(value == AiPrototypeLimits.MaxOutputTokens, Valid(new() { MaxBillableOutputTokens = value }));

    [Theory]
    [InlineData(AiTextOutputKind.Unknown)] [InlineData(AiTextOutputKind.ToolCall)]
    [InlineData(AiTextOutputKind.Image)] [InlineData(AiTextOutputKind.Audio)]
    [InlineData(AiTextOutputKind.Video)] [InlineData(AiTextOutputKind.Mixed)] [InlineData((AiTextOutputKind)99)]
    public void UnsupportedModalitiesAndToolsDeny(AiTextOutputKind kind) => Assert.False(Valid(new() { OutputKind = kind }));

    [Theory]
    [InlineData("framing")] [InlineData("configuration")]
    public void UnsupportedExecutionConfigurationDenies(string defect) => Assert.False(Valid(defect == "framing"
        ? new() { FramingRevision = "unsupported" } : new() { ExecutionConfiguration = "unsupported" }));

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(4294967294L)] [InlineData(4294967295L)]
    public void TimeoutUsesExistingPlatformBoundary(long ms) =>
        Assert.Equal(ms > 0 && ms <= uint.MaxValue - 1, Valid(new() { ProviderTimeout = TimeSpan.FromMilliseconds(ms) }));

    [Theory]
    [InlineData("null", false)] [InlineData("empty", true)] [InlineData("six", true)]
    [InlineData("five", false)] [InlineData("seven", false)] [InlineData("duplicate", false)] [InlineData("empty-id", false)]
    public void AllowlistMustBeAbsentWhileDisabledOrExactlySixUniqueAccounts(string defect, bool expected)
    {
        Guid[]? ids = defect switch
        {
            "null" => null, "empty" => [], "five" => Accounts()[..5], "seven" => Accounts().Append(Id(7)).ToArray(),
            "duplicate" => Enumerable.Repeat(Id(1), 6).ToArray(), "empty-id" => Accounts()[..5].Append(Guid.Empty).ToArray(), _ => Accounts()
        };
        Assert.Equal(expected, Valid(new() { PrototypeAccounts = ids }));
    }

    [Theory]
    [InlineData("valid", true)] [InlineData("id", false)] [InlineData("short", false)]
    [InlineData("long", false)] [InlineData("reverse", false)] [InlineData("offset", false)] [InlineData("default", false)]
    public void WindowRequiresExactExistingUtcDuration(string defect, bool expected)
    {
        var w = Window();
        w = defect switch
        {
            "id" => w with { Id = Guid.Empty }, "short" => w with { EndUtc = Start.AddDays(13) },
            "long" => w with { EndUtc = Start.AddDays(15) }, "reverse" => w with { EndUtc = Start.AddDays(-14) },
            "offset" => w with { StartUtc = Start.ToOffset(TimeSpan.FromHours(1)), EndUtc = w.EndUtc.ToOffset(TimeSpan.FromHours(1)) },
            "default" => w with { StartUtc = default }, _ => w
        };
        Assert.Equal(expected, Valid(new() { PrototypeBudgetWindow = w }));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("private-option-name")]
    public void EvenCompleteEnabledConfigurationCannotClaimCredentialsPricingOrDispatchAuthority(string? name)
    {
        var o = new AiOptions { LiveExecutionEnabled = true, PrototypeAccounts = Accounts(),
            PrototypeBudgetWindow = Window(), ProviderTimeout = TimeSpan.FromSeconds(30) };
        var result = new AiOptionsValidator().Validate(name, o);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, m => m.Contains("credential"));
        Assert.Contains(result.Failures!, m => m.Contains("pricing") && m.Contains("dispatch"));
        if (!string.IsNullOrEmpty(name)) Assert.DoesNotContain(name, string.Join(" ", result.Failures!));
    }

    [Fact]
    public void EnabledMissingFieldsAreExplicitlyRejected()
    {
        var r = new AiOptionsValidator().Validate(null, new() { LiveExecutionEnabled = true });
        Assert.True(r.Failed);
        Assert.Contains(r.Failures!, s => s.Contains("timeout is required"));
        Assert.Contains(r.Failures!, s => s.Contains("allowlist is required"));
        Assert.Contains(r.Failures!, s => s.Contains("window is required"));
    }

    [Fact]
    public void CallerArraysAndDiagnosticsCannotMutateOrExposeValidatedPrivateConfiguration()
    {
        var input = Accounts(); var original = input.ToArray();
        var o = new AiOptions { PrototypeAccounts = input, PrototypeBudgetWindow = Window() };
        Assert.True(Valid(o)); input[0] = Guid.Empty; var exported = o.PrototypeAccounts!; exported[1] = Guid.Empty;
        Assert.Equal(original, o.PrototypeAccounts); Assert.True(Valid(o));
        var invalid = new AiOptions { Model = "recognizable-private-secret" };
        var r = new AiOptionsValidator().Validate("private-name", invalid);
        foreach (var text in new[] { o.ToString(), invalid.ToString(), new AiOptionsValidator().ToString(), string.Join(" ", r.Failures!) })
        {
            Assert.DoesNotContain("recognizable-private-secret", text);
            Assert.DoesNotContain("private-name", text); Assert.DoesNotContain(Id(1).ToString(), text);
        }
    }
}
