using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace Andril.Learning.CV;

/// <summary>
/// DEC-M1-04 offline structural preview only. All generic entry text is unclassified and
/// withheld even when contact inclusion is selected. No field classification is inferred
/// from a section category. Protected release still needs fresh owner authorization and
/// document-specific consent under the production coordination boundary. Never register
/// this as an authorized download, public URL, PDF exporter or authentication mechanism.
/// </summary>
public sealed class CvPreview
{
    [JsonIgnore]
    public string Html { get; }

    private CvPreview(string html) => Html = html;

    /// <summary>
    /// Render only the verified template choice and seven section categories as escaped text.
    /// No private identities, entry contents, counts, external resources or active markup are
    /// projected. This is a local snapshot, not an access grant or production-ready CV layout.
    /// </summary>
    public static CvPreview CreateOffline(LearningCv cv)
    {
        ArgumentNullException.ThrowIfNull(cv);
        var html = new StringBuilder("<article><h1>Private CV preview</h1><p>");
        html.Append(WebUtility.HtmlEncode(cv.Template.ToString()));
        html.Append("</p><p>Unclassified content withheld pending approved field classification.</p>");
        foreach (var category in Enum.GetValues<CvSectionCategory>())
        {
            html.Append("<section><h2>");
            html.Append(WebUtility.HtmlEncode(category.ToString()));
            html.Append("</h2></section>");
        }
        html.Append("</article>");
        return new(html.ToString());
    }

    public override string ToString() => "Private offline CV preview [output omitted; no authorization]";
}
