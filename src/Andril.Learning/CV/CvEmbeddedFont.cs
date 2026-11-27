using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Andril.Learning.CV;

/// <summary>Only pinned embedded font assets are parsed. No caller font/path/URL, system font lookup or network fallback.</summary>
internal sealed class CvEmbeddedFont
{
    internal byte[] Bytes { get; }
    internal string Name { get; }
    private readonly Dictionary<string,int> tables=[];
    private readonly int cmap,units,metrics,numberOfMetrics,glyphCount;
    internal int Ascent { get; }
    internal int Descent { get; }
    internal int[] Bounds { get; }
    private ushort U16(int at)=>BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(at,2));
    private short I16(int at)=>BinaryPrimitives.ReadInt16BigEndian(Bytes.AsSpan(at,2));
    private uint U32(int at)=>BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(at,4));
    private int Scaled(int value)=>(int)Math.Round(value*1000.0/units,MidpointRounding.AwayFromZero);

    internal CvEmbeddedFont(CvTemplate template)
    {
        var spec=template switch
        {
            CvTemplate.Professional => ("NotoSerif-Regular","C8F669CEB2C9C60CCF55198B305E08A997FFCA79A38CC7EEB551E643CBE66505"),
            CvTemplate.Creative => ("NotoSansMono-Regular","D9E2B23D19F8230BE7146F409A52B1D23117E635E28F2E2892CF91B7382F325B"),
            _ => ("NotoSans-Regular","B85C38ECEA8A7CFB39C24E395A4007474FA5A4FC864F6EE33309EB4948D232D5")
        };
        Name=spec.Item1;
        using var stream=typeof(CvEmbeddedFont).Assembly.GetManifestResourceStream("Andril.Learning.CV.Fonts."+Name+".ttf")??throw new InvalidOperationException("Verified CV font is unavailable.");
        using var memory=new MemoryStream();stream.CopyTo(memory);Bytes=memory.ToArray();
        if(Convert.ToHexString(SHA256.HashData(Bytes))!=spec.Item2||U32(0)!=0x00010000)throw new InvalidOperationException("CV font integrity failed.");
        var count=U16(4);
        for(var i=0;i<count;i++)
        {
            var p=12+i*16;var key=Encoding.ASCII.GetString(Bytes,p,4);var offset=checked((int)U32(p+8));var size=checked((int)U32(p+12));
            if(offset<0||size<0||offset>Bytes.Length-size)throw new InvalidOperationException("Invalid pinned CV font table.");tables.Add(key,offset);
        }
        var head=tables["head"];units=U16(head+18);if(units==0)throw new InvalidOperationException("Invalid CV font metrics.");
        var hhea=tables["hhea"];Ascent=Scaled(I16(hhea+4));Descent=Scaled(I16(hhea+6));numberOfMetrics=U16(hhea+34);
        metrics=tables["hmtx"];glyphCount=U16(tables["maxp"]+4);
        Bounds=[Scaled(I16(head+36)),Scaled(I16(head+38)),Scaled(I16(head+40)),Scaled(I16(head+42))];
        if((U16(tables["OS/2"]+8)&0x0202)!=0)throw new NotSupportedException("Pinned CV font does not permit outline embedding.");
        var maps=tables["cmap"];var candidates=new List<int>();
        for(var i=0;i<U16(maps+2);i++)
        {var offset=checked(maps+(int)U32(maps+4+i*8+4));if(U16(offset) is 4 or 12)candidates.Add(offset);}
        cmap=candidates.OrderByDescending(x=>U16(x)).First();
    }
    internal ushort Glyph(Rune rune)
    {
        var scalar=rune.Value;var category=Rune.GetUnicodeCategory(rune);
        // Explicit supported shaping contract: precomposed Latin/Greek/Cyrillic and
        // supported punctuation/currency. Complex scripts/combining/bidi never degrade.
        if(category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.Control ||
            !(scalar is >=32 and <=0x052F or >=0x1E00 and <=0x1FFF or >=0x2000 and <=0x206F or >=0x20A0 and <=0x20CF))
            throw new NotSupportedException("CV PDF content requires unsupported script shaping.");
        uint glyph=0;
        if(U16(cmap)==12)
        {
            var groups=U32(cmap+12);
            for(var i=0;i<groups;i++){var p=checked(cmap+16+i*12);var start=U32(p);var end=U32(p+4);if(scalar>=start&&scalar<=end){glyph=U32(p+8)+(uint)scalar-start;break;}}
        }
        else
        {
            var segments=U16(cmap+6)/2;var ends=cmap+14;var starts=ends+segments*2+2;var deltas=starts+segments*2;var ranges=deltas+segments*2;
            for(var i=0;i<segments;i++)
            {
                if(scalar<U16(starts+i*2)||scalar>U16(ends+i*2))continue;
                var delta=I16(deltas+i*2);var range=U16(ranges+i*2);
                glyph=range==0?(uint)((scalar+delta)&0xFFFF):U16(ranges+i*2+range+(scalar-U16(starts+i*2))*2);
                if(range!=0&&glyph!=0)glyph=(uint)((glyph+delta)&0xFFFF);break;
            }
        }
        if(glyph==0||glyph>=glyphCount)throw new NotSupportedException("Pinned CV font has no glyph for this content.");
        return checked((ushort)glyph);
    }
    internal int Width(ushort glyph)=>Scaled(U16(metrics+Math.Min(glyph,numberOfMetrics-1)*4));
}
