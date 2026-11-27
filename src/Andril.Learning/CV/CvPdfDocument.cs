using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Andril.Learning.CV;

/// <summary>In-memory private PDF snapshot of approved printable-ASCII structured fields.
/// Not an authorization grant. Uses the same allowlist as preview; unclassified text is never embedded,
/// including in metadata, annotations or hidden streams. No files, fonts, URLs or resources
/// are fetched. Future approved Unicode field rendering needs a separate font/layout contract.</summary>
public sealed class CvPdfDocument
{
    private readonly byte[] content;
    [JsonIgnore] public byte[] Bytes => (byte[])content.Clone();
    private CvPdfDocument(byte[] content) => this.content = content;

    /// <summary>Offline snapshot only. Protected delivery must use the repository's fresh
    /// authorization and persisted consent boundary, never a caller-held CV as access evidence.</summary>
    public static CvPdfDocument CreateOffline(LearningCv cv, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projection = CvOutputProjection.From(cv);
        var font = projection.Template switch
        {
            CvTemplate.Professional => "Times-Roman",
            CvTemplate.Creative => "Courier",
            _ => "Helvetica"
        };
        var pages = new List<string[]>();
        for (var part = 0; part < 2; part++)
        {
            var lines = new List<string>();
            foreach (var section in projection.Sections.Skip(part * 4).Take(4))
            {
                lines.Add(section);
                foreach (var field in projection.Content[section])
                    foreach (var line in field.Replace("\r\n","\n").Replace('\r','\n').Split('\n'))
                    {
                        // Built-in PDF fonts have a deliberately explicit supported alphabet.
                        // Never replace unsupported Unicode with '?' or discard stored content.
                        if (line.Any(c => c < ' ' || c > '~')) throw new NotSupportedException("A verified embedded font is required for this PDF content.");
                        if (line.Length == 0) lines.Add("");
                        else for (var offset = 0; offset < line.Length; offset += 86) lines.Add(line.Substring(offset,Math.Min(86,line.Length-offset)));
                    }
                lines.Add("");
            }
            foreach (var page in lines.Chunk(42)) pages.Add(page);
        }
        var kids = string.Join(" ",Enumerable.Range(0,pages.Count).Select(p => $"{4+p*2} 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /{font} >>"
        };
        // Preserve seven categories with content-driven pagination and no text truncation.
        for (var page = 0; page < pages.Count; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new StringBuilder();
            Text(stream, 20, 750, "Private CV preview");
            Text(stream, 14, 720, projection.Template.ToString());
            Text(stream, 9, 695, CvOutputProjection.Notice);
            for (var i = 0; i < pages[page].Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Text(stream, 10, 665 - i * 14, pages[page][i]);
            }
            Text(stream, 10, 40, $"Page {page+1} of {pages.Count}");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + page * 2} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream.ToString())} >>\nstream\n{stream}endstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        cancellationToken.ThrowIfCancellationRequested();
        return new(Encoding.ASCII.GetBytes(pdf.ToString()));
    }

    private static void Text(StringBuilder stream, int size, int y, string label)
    {
        // Approved projected ASCII fields are escaped as inert PDF text, never operators.
        if (label.Any(c => c < ' ' || c > '~')) throw new InvalidOperationException("Unsupported structural PDF label.");
        var escaped = label.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        stream.Append("BT /F1 ").Append(size).Append(" Tf 50 ").Append(y).Append(" Td (").Append(escaped).Append(") Tj ET\n");
    }

    public override string ToString() => "Private CV PDF [content omitted]";
}
