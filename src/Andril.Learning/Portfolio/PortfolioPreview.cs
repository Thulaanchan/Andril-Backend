using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace Andril.Learning.Portfolio;

/// <summary>Private owner-editing preview of existing manual project fields. Never a public
/// Portfolio, cross-module projection, CV consent mechanism or media-fetching facility.
/// URLs are displayed as inert escaped text, never links, images, CSS or embedded resources.
/// Protected delivery requires fresh repository authorization, not a caller-held snapshot.</summary>
public sealed class PortfolioPreview
{
    [JsonIgnore] public string Html { get; }
    private PortfolioPreview(string html) => Html = html;

    public static PortfolioPreview CreateOffline(LearningPortfolio portfolio, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(portfolio); ct.ThrowIfCancellationRequested();
        // Local presentation choices only; no pricing, entitlement or business policy.
        var style = portfolio.Template switch
        {
            PortfolioTemplate.Developer => "font-family:monospace;color:#172554;background:#f8fafc;padding:24px;border-top:6px solid #2563eb",
            PortfolioTemplate.Creative => "font-family:sans-serif;color:#4c1d95;background:#faf5ff;padding:24px;border-left:6px solid #a855f7",
            PortfolioTemplate.Professional => "font-family:serif;color:#1f2937;background:#fff;padding:24px;border-top:2px solid #374151",
            _ => throw new ArgumentException("Unsupported Portfolio template.", nameof(portfolio))
        };
        var html = new StringBuilder("<article style=\"").Append(style).Append("\"><h1>Private Portfolio preview</h1><p>");
        html.Append(WebUtility.HtmlEncode(portfolio.Template.ToString())).Append("</p>");
        foreach (var project in portfolio.Projects)
        {
            ct.ThrowIfCancellationRequested();
            html.Append("<section><h2>").Append(WebUtility.HtmlEncode(project.Title)).Append("</h2><p style=\"white-space:pre-wrap\">")
                .Append(WebUtility.HtmlEncode(project.Description)).Append("</p>");
            References(html, "Media references", project.MediaReferences, ct);
            References(html, "External references", project.ExternalLinks, ct);
            html.Append("</section>");
        }
        ct.ThrowIfCancellationRequested(); html.Append("</article>"); return new(html.ToString());
    }

    private static void References(StringBuilder html, string heading, IEnumerable<string> references, CancellationToken ct)
    {
        html.Append("<h3>").Append(heading).Append("</h3><ul>");
        foreach (var reference in references) { ct.ThrowIfCancellationRequested(); html.Append("<li>").Append(WebUtility.HtmlEncode(reference)).Append("</li>"); }
        html.Append("</ul>");
    }
    public override string ToString() => "Private Portfolio preview [content omitted]";
}
