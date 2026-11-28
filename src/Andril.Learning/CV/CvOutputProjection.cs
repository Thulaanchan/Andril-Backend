namespace Andril.Learning.CV;

/// <summary>Confirmed structured-field output allowlist shared by HTML and PDF.
/// Legacy Entry.Text remains unclassified even with contact consent. Only explicitly
/// reviewed approved fields are considered; private identities/bookkeeping are omitted.</summary>
internal sealed class CvOutputProjection
{
    internal const string Notice = "Unclassified content withheld pending approved field classification.";
    internal CvTemplate Template { get; }
    internal IReadOnlyList<string> Sections { get; }
    internal IReadOnlyDictionary<string,IReadOnlyList<string>> Content { get; }

    private CvOutputProjection(LearningCv cv)
    {
        Template = cv.Template;
        Sections = Array.AsReadOnly(Enum.GetValues<CvSectionCategory>().Select(c => c.ToString()).ToArray());
        Content = cv.Sections.ToDictionary(s => s.Category.ToString(),s => (IReadOnlyList<string>)Array.AsReadOnly(s.Entries.SelectMany(e =>
            CvStructuredFields.Names(s.Category).Where(key => e.Fields.TryGetValue(key,out var text) && !string.IsNullOrWhiteSpace(text) && CvStructuredFields.MayRender(key,text,cv.IncludeContactInformation))
                .Select(key => key+": "+e.Fields[key])).ToArray()));
    }

    internal static CvOutputProjection From(LearningCv cv)
    {
        ArgumentNullException.ThrowIfNull(cv);
        // Projection reads approved structured fields, never classifies legacy Entry.Text.
        return new(cv);
    }
}
