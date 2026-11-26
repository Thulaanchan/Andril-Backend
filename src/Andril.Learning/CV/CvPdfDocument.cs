using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Andril.Learning.CV;

/// <summary>In-memory, private structural PDF snapshot. Not an authorization grant or a full
/// field renderer. Uses the same allowlist as preview; unclassified text is never embedded,
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
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R] /Count 2 >>",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /{font} >>"
        };
        // A fixed two-page structural layout preserves all seven existing labels. It is not
        // a truncation/count policy for future approved CV fields or a data-dependent layout.
        for (var page = 0; page < 2; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new StringBuilder();
            Text(stream, 20, 750, "Private CV preview");
            Text(stream, 14, 720, projection.Template.ToString());
            Text(stream, 9, 695, CvOutputProjection.Notice);
            var start = page * 4;
            for (var i = start; i < Math.Min(start + 4, projection.Sections.Count); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Text(stream, 14, 625 - (i - start) * 120, projection.Sections[i]);
            }
            Text(stream, 10, 40, page == 0 ? "Page 1 of 2" : "Page 2 of 2");
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
        // Only compile-time ASCII labels and defined enum names enter PDF string syntax.
        if (label.Any(c => c < ' ' || c > '~')) throw new InvalidOperationException("Unsupported structural PDF label.");
        var escaped = label.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        stream.Append("BT /F1 ").Append(size).Append(" Tf 50 ").Append(y).Append(" Td (").Append(escaped).Append(") Tj ET\n");
    }

    public override string ToString() => "Private CV PDF [content omitted]";
}
