using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Andril.Learning.CV;

/// <summary>Pinned-font, in-memory Type0/CID PDF. No font probing, file paths, resource fetching or user metadata.</summary>
internal static class CvUnicodePdfRenderer
{
    internal static byte[] Render(CvOutputProjection projection,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var font=new CvEmbeddedFont(projection.Template);var pages=new List<string[]>();
        for(var part=0;part<2;part++)
        {
            var lines=new List<string>();
            foreach(var section in projection.Sections.Skip(part*4).Take(4))
            {
                lines.Add(section);
                foreach(var field in projection.Content[section])
                    foreach(var paragraph in field.Replace("\r\n","\n").Replace('\r','\n').Split('\n'))
                    {
                        var line=new StringBuilder();var width=0;
                        foreach(var rune in paragraph.EnumerateRunes())
                        {
                            ct.ThrowIfCancellationRequested();var advance=font.Width(font.Glyph(rune));
                            if(width+advance>51200&&line.Length>0){lines.Add(line.ToString());line.Clear();width=0;}
                            line.Append(rune.ToString());width+=advance;
                        }
                        lines.Add(line.ToString());
                    }
                lines.Add("");
            }
            pages.AddRange(lines.Chunk(42));
        }
        var texts=pages.SelectMany(x=>x).Concat(["Private CV preview",projection.Template.ToString(),CvOutputProjection.Notice])
            .Concat(Enumerable.Range(1,pages.Count).Select(x=>$"Page {x} of {pages.Count}"));
        var unicode=texts.SelectMany(x=>x.EnumerateRunes()).Select(x=>x.Value).Distinct().Order().ToArray();
        if(unicode.Length>65534)throw new NotSupportedException("CV font encoding capacity exceeded.");
        var cids=unicode.Select((scalar,index)=>(scalar,cid:(ushort)(index+1))).ToDictionary(x=>x.scalar,x=>x.cid);
        var glyphs=unicode.Select(x=>font.Glyph(new Rune(x))).ToArray();
        var cidMap=new byte[(unicode.Length+1)*2];
        for(var i=0;i<glyphs.Length;i++)BinaryPrimitives.WriteUInt16BigEndian(cidMap.AsSpan((i+1)*2),glyphs[i]);
        var cmap=new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /AndrilCVUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach(var group in unicode.Chunk(100))
        {
            cmap.Append(group.Length).Append(" beginbfchar\n");
            foreach(var scalar in group)cmap.Append('<').Append(cids[scalar].ToString("X4",CultureInfo.InvariantCulture)).Append("> <").Append(scalar.ToString("X4",CultureInfo.InvariantCulture)).Append(">\n");
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var compressed=Compress(font.Bytes);var widths=string.Join(" ",glyphs.Select(font.Width));
        var objects=new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Kids [{string.Join(" ",Enumerable.Range(0,pages.Count).Select(x=>$"{9+x*2} 0 R"))}] /Count {pages.Count} >>"),
            Ascii($"<< /Type /Font /Subtype /Type0 /BaseFont /{font.Name} /Encoding /Identity-H /DescendantFonts [4 0 R] /ToUnicode 8 0 R >>"),
            Ascii($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{font.Name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 5 0 R /CIDToGIDMap 7 0 R /DW 1000 /W [1 [{widths}]] >>"),
            Ascii($"<< /Type /FontDescriptor /FontName /{font.Name} /Flags 32 /FontBBox [{string.Join(" ",font.Bounds)}] /ItalicAngle 0 /Ascent {font.Ascent} /Descent {font.Descent} /CapHeight {font.Ascent} /StemV 80 /FontFile2 6 0 R >>"),
            Stream(compressed,$"/Filter /FlateDecode /Length1 {font.Bytes.Length}"),Stream(cidMap),Stream(Ascii(cmap.ToString()))
        };
        for(var page=0;page<pages.Count;page++)
        {
            ct.ThrowIfCancellationRequested();var content=new StringBuilder();
            Text(content,projection.Template==CvTemplate.Creative?22:20,750,"Private CV preview",cids);
            Text(content,14,720,projection.Template.ToString(),cids);Text(content,9,695,CvOutputProjection.Notice,cids);
            for(var line=0;line<pages[page].Length;line++)Text(content,10,665-line*14,pages[page][line],cids);
            Text(content,10,40,$"Page {page+1} of {pages.Count}",cids);
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {10+page*2} 0 R >>"));
            objects.Add(Stream(Ascii(content.ToString())));
        }
        using var pdf=new MemoryStream();Write(pdf,"%PDF-1.7\n");var offsets=new List<long>{0};
        for(var i=0;i<objects.Count;i++)
        {ct.ThrowIfCancellationRequested();offsets.Add(pdf.Position);Write(pdf,$"{i+1} 0 obj\n");pdf.Write(objects[i]);Write(pdf,"\nendobj\n");}
        var xref=pdf.Position;Write(pdf,$"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");
        foreach(var offset in offsets.Skip(1))Write(pdf,offset.ToString("D10",CultureInfo.InvariantCulture)+" 00000 n \n");
        Write(pdf,$"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        ct.ThrowIfCancellationRequested();return pdf.ToArray();
    }
    private static void Text(StringBuilder content,int size,int y,string text,IReadOnlyDictionary<int,ushort> cids)
    {
        content.Append("BT /F1 ").Append(size).Append(" Tf 50 ").Append(y).Append(" Td <");
        foreach(var rune in text.EnumerateRunes())content.Append(cids[rune.Value].ToString("X4",CultureInfo.InvariantCulture));
        content.Append("> Tj ET\n");
    }
    private static byte[] Compress(byte[] bytes)
    {using var memory=new MemoryStream();using(var compressed=new ZLibStream(memory,CompressionLevel.Optimal,true))compressed.Write(bytes);return memory.ToArray();}
    private static byte[] Stream(byte[] bytes,string attributes="")
    {using var stream=new MemoryStream();Write(stream,$"<< /Length {bytes.Length} {attributes} >>\nstream\n");stream.Write(bytes);Write(stream,"\nendstream");return stream.ToArray();}
    private static byte[] Ascii(string text)=>Encoding.ASCII.GetBytes(text);
    private static void Write(MemoryStream stream,string text)=>stream.Write(Ascii(text));
}
