using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using HarfBuzzSharp;
using static Andril.Learning.CV.CvUnicodePdfRenderer;

namespace Andril.Learning.CV;

/// <summary>Explicit LTR OpenType shaping for Sinhala, Tamil and combining Latin.
/// Uses pinned embedded fonts only. This is not a bidi engine or a universal-script fallback.</summary>
internal static class CvShapedPdfRenderer
{
    internal static bool IsRequired(CvOutputProjection projection) => projection.Content.Values.SelectMany(x=>x)
        .Any(text=>text.EnumerateRunes().Any(r=>r.Value is >=0x0B80 and <=0x0BFF or >=0x0D80 and <=0x0DFF ||
            Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark));

    private sealed class FaceFont : IDisposable
    {
        internal CvEmbeddedFont Asset { get; }
        internal Font Font { get; }
        private readonly Blob blob;
        private readonly Face face;
        internal readonly Dictionary<(ushort Glyph,string Text),ushort> Codes=[];
        internal FaceFont(CvTemplate template,string? script)
        {
            Asset=new(template,script);
            // In the pinned binding, Blob.FromStream hands native code a ReadOnly
            // pointer to an array unpinned on return. Duplicate copies while pinned;
            // the native blob then owns stable bytes independently of managed GC.
            var pin=GCHandle.Alloc(Asset.Bytes,GCHandleType.Pinned);
            try{blob=new Blob(pin.AddrOfPinnedObject(),Asset.Bytes.Length,MemoryMode.Duplicate);}
            finally{pin.Free();}
            try
            {
                if(blob.Length!=Asset.Bytes.Length)throw new InvalidOperationException("CV font allocation failed.");
                face=new Face(blob,0);
                try
                {
                    Font=new Font(face);
                    try{Font.SetFunctionsOpenType();Font.SetScale(1000,1000);}
                    catch{Font.Dispose();throw;}
                }
                catch{face.Dispose();throw;}
            }
            catch{blob.Dispose();throw;}
        }
        internal ushort Code(ushort glyph,string text)
        {
            if(Codes.TryGetValue((glyph,text),out var cid))return cid;
            if(Codes.Count>=65534)throw new NotSupportedException("CV font encoding capacity exceeded.");
            cid=checked((ushort)(Codes.Count+1));Codes.Add((glyph,text),cid);return cid;
        }
        public void Dispose(){Font.Dispose();face.Dispose();blob.Dispose();}
    }
    private sealed record Glyph(int Font,ushort Id,string Text,int Advance,int X,int Y);
    private sealed record Line(string Text,IReadOnlyList<Glyph> Glyphs);
    private sealed class Layout(CvTemplate template,CancellationToken ct):IDisposable
    {
        internal readonly List<FaceFont> Fonts=[];
        private readonly Dictionary<string,int> indices=[];
        private int Index(string script)
        {
            if(indices.TryGetValue(script,out var index))return index;
            var font=new FaceFont(template,script=="Latin"?null:script);
            index=Fonts.Count;Fonts.Add(font);indices.Add(script,index);return index;
        }
        private static string Script(string element)
        {
            var first=element.EnumerateRunes().First().Value;
            if(first is >=0x0B80 and <=0x0BFF)return "Tamil";
            if(first is >=0x0D80 and <=0x0DFF)return "Sinhala";
            if(first is >=32 and <=0x052F or >=0x1E00 and <=0x1FFF or >=0x2000 and <=0x206F or >=0x20A0 and <=0x20CF)return "Latin";
            throw new NotSupportedException("CV PDF script is unsupported.");
        }
        internal Line Shape(string text)
        {
            ct.ThrowIfCancellationRequested();var result=new List<Glyph>();var runs=new List<(string Script,string Text)>();
            var elements=StringInfo.GetTextElementEnumerator(text);
            while(elements.MoveNext())
            {
                var element=elements.GetTextElement();var script=Script(element);
                foreach(var rune in element.EnumerateRunes())
                {
                    var category=Rune.GetUnicodeCategory(rune);
                    if(category is UnicodeCategory.Control or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned ||
                        category==UnicodeCategory.Format && !(script is "Sinhala" or "Tamil" && rune.Value is 0x200C or 0x200D))
                        throw new NotSupportedException("CV PDF control or direction is unsupported.");
                }
                if(runs.Count>0 && runs[^1].Script==script)runs[^1]=(script,runs[^1].Text+element);
                else runs.Add((script,element));
            }
            foreach(var run in runs)
            {
                ct.ThrowIfCancellationRequested();var index=Index(run.Script);
                using var buffer=new HarfBuzzSharp.Buffer();buffer.AddUtf16(run.Text);buffer.GuessSegmentProperties();
                if(buffer.Direction!=Direction.LeftToRight)throw new NotSupportedException("CV PDF direction is unsupported.");
                Fonts[index].Font.Shape(buffer);var infos=buffer.GlyphInfos;var positions=buffer.GlyphPositions;
                var clusters=infos.Select(x=>checked((int)x.Cluster)).Append(run.Text.Length).Distinct().Order().ToArray();
                var emitted=new HashSet<uint>();
                for(var i=0;i<infos.Length;i++)
                {
                    if(infos[i].Codepoint==0 || infos[i].Codepoint>ushort.MaxValue)throw new NotSupportedException("CV PDF glyph is unavailable.");
                    var start=checked((int)infos[i].Cluster);var next=clusters[Array.BinarySearch(clusters,start)+1];
                    var logical=emitted.Add(infos[i].Cluster)?run.Text[start..next]:"";
                    result.Add(new(index,(ushort)infos[i].Codepoint,logical,positions[i].XAdvance,positions[i].XOffset,positions[i].YOffset));
                }
            }
            return new(text,result);
        }
        internal IEnumerable<Line> Wrap(string text)
        {
            var start=0;var boundaries=StringInfo.ParseCombiningCharacters(text);
            while(start<boundaries.Length)
            {
                ct.ThrowIfCancellationRequested();var low=start+1;var high=boundaries.Length;var end=low;
                // Binary search complete grapheme boundaries, then shape the final line again.
                while(low<=high)
                {
                    var mid=low+(high-low)/2;var to=mid==boundaries.Length?text.Length:boundaries[mid];
                    var shaped=Shape(text[boundaries[start]..to]);
                    if(shaped.Glyphs.Sum(x=>(long)x.Advance)<=51200){end=mid;low=mid+1;}else high=mid-1;
                }
                var stop=end==boundaries.Length?text.Length:boundaries[end];var line=Shape(text[boundaries[start]..stop]);
                if(line.Glyphs.Sum(x=>(long)x.Advance)>51200)throw new NotSupportedException("CV PDF cluster exceeds page width.");
                yield return line;start=end;
            }
            if(text.Length==0)yield return Shape("");
        }
        public void Dispose(){foreach(var font in Fonts)font.Dispose();}
    }

    internal static byte[] Render(CvOutputProjection projection,CancellationToken ct)
    {
        using var layout=new Layout(projection.Template,ct);var title=layout.Shape("Private CV preview");
        var template=layout.Shape(projection.Template.ToString());var notice=layout.Shape(CvOutputProjection.Notice);
        var pages=new List<Line[]>();
        for(var part=0;part<2;part++)
        {
            var lines=new List<Line>();
            foreach(var section in projection.Sections.Skip(part*4).Take(4))
            {
                lines.Add(layout.Shape(section));
                foreach(var field in projection.Content[section])
                foreach(var paragraph in field.Replace("\r\n","\n").Replace('\r','\n').Split('\n'))lines.AddRange(layout.Wrap(paragraph));
                lines.Add(layout.Shape(""));
            }
            pages.AddRange(lines.Chunk(30)); // 18pt leading leaves room for Indic ascenders/marks.
        }
        var contents=new List<byte[]>();
        for(var p=0;p<pages.Count;p++)
        {
            ct.ThrowIfCancellationRequested();var s=new StringBuilder();
            Draw(s,title,20,750,layout);Draw(s,template,14,720,layout);Draw(s,notice,9,695,layout);
            for(var line=0;line<pages[p].Length;line++)Draw(s,pages[p][line],10,665-line*18,layout);
            Draw(s,layout.Shape($"Page {p+1} of {pages.Count}"),10,40,layout);contents.Add(Ascii(s.ToString()));
        }
        var firstPage=3+layout.Fonts.Count*6;var objects=new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Kids [{string.Join(" ",Enumerable.Range(0,pages.Count).Select(x=>$"{firstPage+x*2} 0 R"))}] /Count {pages.Count} >>")
        };
        for(var f=0;f<layout.Fonts.Count;f++)AddFont(objects,layout.Fonts[f],3+f*6);
        var resources=string.Join(" ",Enumerable.Range(0,layout.Fonts.Count).Select(i=>$"/F{i} {3+i*6} 0 R"));
        for(var p=0;p<pages.Count;p++)
        {
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << {resources} >> >> /Contents {firstPage+p*2+1} 0 R >>"));
            objects.Add(Stream(contents[p]));
        }
        using var pdf=new MemoryStream();Write(pdf,"%PDF-1.7\n");var offsets=new List<long>{0};
        for(var i=0;i<objects.Count;i++){ct.ThrowIfCancellationRequested();offsets.Add(pdf.Position);Write(pdf,$"{i+1} 0 obj\n");pdf.Write(objects[i]);Write(pdf,"\nendobj\n");}
        var xref=pdf.Position;Write(pdf,$"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach(var offset in offsets.Skip(1))Write(pdf,offset.ToString("D10",CultureInfo.InvariantCulture)+" 00000 n \n");
        Write(pdf,$"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");return pdf.ToArray();
    }
    private static string Hex(string text)=>Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));
    private static string Number(double n)=>n.ToString("0.###",CultureInfo.InvariantCulture);
    private static void Draw(StringBuilder s,Line line,int size,int y,Layout layout)
    {
        // ActualText preserves logical order through ligatures and reordered Indic glyphs.
        s.Append("/Span << /ActualText <FEFF").Append(Hex(line.Text)).Append("> >> BDC\n");double x=50;
        foreach(var g in line.Glyphs)
        {
            var code=layout.Fonts[g.Font].Code(g.Id,g.Text);
            s.Append("BT /F").Append(g.Font).Append(' ').Append(size).Append(" Tf 1 0 0 1 ")
                .Append(Number(x+g.X*size/1000.0)).Append(' ').Append(Number(y+g.Y*size/1000.0)).Append(" Tm <")
                .Append(code.ToString("X4",CultureInfo.InvariantCulture)).Append("> Tj ET\n");x+=g.Advance*size/1000.0;
        }
        s.Append("EMC\n");
    }
    private static void AddFont(List<byte[]> objects,FaceFont font,int id)
    {
        var a=font.Asset;var codes=font.Codes.OrderBy(x=>x.Value).ToArray();var map=new byte[(codes.Length+1)*2];
        foreach(var code in codes)BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(code.Value*2),code.Key.Glyph);
        var cmap=new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /AndrilCVShaped def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach(var group in codes.Chunk(100))
        {
            cmap.Append(group.Length).Append(" beginbfchar\n");
            foreach(var c in group)cmap.Append('<').Append(c.Value.ToString("X4",CultureInfo.InvariantCulture)).Append("> <").Append(Hex(c.Key.Text)).Append(">\n");
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        objects.Add(Ascii($"<< /Type /Font /Subtype /Type0 /BaseFont /{a.Name} /Encoding /Identity-H /DescendantFonts [{id+1} 0 R] /ToUnicode {id+5} 0 R >>"));
        objects.Add(Ascii($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{a.Name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {id+2} 0 R /CIDToGIDMap {id+4} 0 R /DW 1000 /W [1 [{string.Join(" ",codes.Select(x=>a.Width(x.Key.Glyph)))}]] >>"));
        objects.Add(Ascii($"<< /Type /FontDescriptor /FontName /{a.Name} /Flags 32 /FontBBox [{string.Join(" ",a.Bounds)}] /ItalicAngle 0 /Ascent {a.Ascent} /Descent {a.Descent} /CapHeight {a.Ascent} /StemV 80 /FontFile2 {id+3} 0 R >>"));
        objects.Add(Stream(Compress(a.Bytes),$"/Filter /FlateDecode /Length1 {a.Bytes.Length}"));objects.Add(Stream(map));objects.Add(Stream(Ascii(cmap.ToString())));
    }
}
