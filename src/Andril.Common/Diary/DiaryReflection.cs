using System.Globalization;
using System.Text.Json.Serialization;

namespace Andril.Common.Diary;

/// <summary>
/// CF-13 / DIA-R01, R03, R06, R08-R10: immutable private reflection content.
/// Ownership, module/calendar-date context and one-reflection-per-date enforcement
/// belong to the later daily entry aggregate and authoritative persistence integration.
/// This value grants no access and performs no sharing, AI ingestion or rewards.
/// </summary>
public sealed class DiaryReflection
{
    [JsonIgnore]
    public string? Title { get; }
    [JsonIgnore]
    public string Body { get; }

    /// <summary>Optional title and required nonblank body. Lengths follow the existing
    /// Common text-element counting convention; text is neither trimmed nor normalized.</summary>
    public DiaryReflection(string? title, string body)
    {
        if (title is not null && new StringInfo(title).LengthInTextElements > 100)
            throw new ArgumentException("Reflection title cannot exceed 100 characters.", nameof(title));
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("A nonblank reflection body is required.", nameof(body));
        if (new StringInfo(body).LengthInTextElements > 10_000)
            throw new ArgumentException("Reflection body cannot exceed 10,000 characters.", nameof(body));
        Title = title;
        Body = body;
    }

    /// <summary>Return validated replacement content without changing this reflection.
    /// Rejected edits preserve the complete previous value.</summary>
    public DiaryReflection WithContent(string? title, string body) => new(title, body);

    public override string ToString() => "Private Diary reflection [title and body omitted]";
}
