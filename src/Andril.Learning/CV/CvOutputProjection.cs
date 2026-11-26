namespace Andril.Learning.CV;

/// <summary>The current source-backed output allowlist, shared by HTML and PDF.
/// Only the template and seven category labels have an established rendering contract.
/// All Entry.Text is unclassified, including embedded contact details in any section;
/// consent does not classify it. IDs, ownership and consent bookkeeping are internal.
/// No proposed name/contact/education field schema is implicitly approved here.</summary>
internal sealed class CvOutputProjection
{
    internal const string Notice = "Unclassified content withheld pending approved field classification.";
    internal CvTemplate Template { get; }
    internal IReadOnlyList<string> Sections { get; }

    private CvOutputProjection(CvTemplate template)
    {
        Template = template;
        Sections = Array.AsReadOnly(Enum.GetValues<CvSectionCategory>().Select(c => c.ToString()).ToArray());
    }

    internal static CvOutputProjection From(LearningCv cv)
    {
        ArgumentNullException.ThrowIfNull(cv);
        // Intentionally never inspect entries, private identity, or caller-supplied field labels.
        return new(cv.Template);
    }
}
