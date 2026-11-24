using System.Globalization;
using System.Text.Json.Serialization;

namespace Andril.Common.Diary;

/// <summary>
/// CF-13 / DIA-R05, R06, R08-R10: immutable private daily-plan content.
/// The later daily entry aggregate owns module/profile/date context, plan membership,
/// deletion and the daily plan limit. This value does not establish authenticated
/// ownership, perform external requests or grant sharing, AI access or rewards.
/// </summary>
public sealed class DiaryPlan
{
    public Guid Id { get; }
    [JsonIgnore]
    public string Title { get; }
    [JsonIgnore]
    public string? Description { get; }

    /// <summary>Required nonblank title and optional description. Lengths follow the
    /// Common text-element convention; supplied text is neither trimmed nor normalized.</summary>
    public DiaryPlan(Guid id, string title, string? description = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A Diary plan ID is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A nonblank Diary plan title is required.", nameof(title));
        if (new StringInfo(title).LengthInTextElements > 120)
            throw new ArgumentException("Diary plan title cannot exceed 120 characters.", nameof(title));
        if (description is not null && new StringInfo(description).LengthInTextElements > 1_000)
            throw new ArgumentException("Diary plan description cannot exceed 1,000 characters.", nameof(description));
        Id = id;
        Title = title;
        Description = description;
    }

    /// <summary>Return validated replacement content with the same plan identity.
    /// This plan and its complete valid state remain unchanged, including after rejected edits.</summary>
    public DiaryPlan WithContent(string title, string? description = null) => new(Id, title, description);

    public override string ToString() => "Private Diary plan [identity, title and description omitted]";
}
