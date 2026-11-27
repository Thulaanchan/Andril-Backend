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
        var projection = CvOutputProjection.From(cv);
        var html = new StringBuilder("<article><h1>Private CV preview</h1><p>");
        var family = projection.Template switch { CvTemplate.Professional => "serif", CvTemplate.Creative => "monospace", _ => "sans-serif" };
        // Static template styling only: no user CSS, links or resource fetches.
        html.Replace("<article>","<article><div style=\"font-family:"+family+"\">");
        html.Append(WebUtility.HtmlEncode(projection.Template.ToString()));
        html.Append("</p><p>").Append(WebUtility.HtmlEncode(CvOutputProjection.Notice)).Append("</p>");
        foreach (var category in projection.Sections)
        {
            html.Append("<section><h2>");
            html.Append(WebUtility.HtmlEncode(category.ToString()));
            html.Append("</h2>");
            foreach(var line in projection.Content[category]) html.Append("<p>").Append(WebUtility.HtmlEncode(line)).Append("</p>");
            html.Append("</section>");
        }
        html.Append("</div></article>");
        return new(html.ToString());
    }

    public override string ToString() => "Private offline CV preview [output omitted; no authorization]";
}
