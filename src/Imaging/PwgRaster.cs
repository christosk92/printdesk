using System.Buffers.Binary;
using System.Text;

namespace PrintDesk.Imaging;

/// <summary>
/// PWG Raster (PWG 5102.4), the one format every IPP Everywhere printer must take: "RaS2", then per page a 1796-byte
/// header and the pixels, each line run-length coded (a line-repeat byte, then pixel runs). 8-bit sRGB or sGray.
/// </summary>
static class PwgRaster
{
    /// <summary>The file's sync word; write it once, then each page.</summary>
    public static void Begin(Stream s) => s.Write("RaS2"u8);

    public static void WritePage(Stream s, PageImage page, bool color, int totalPages, int copies)
    {
        int bpp = color ? 3 : 1;
        var h = new byte[1796];
        Ascii(h, 0, "PwgRaster");                                   // MediaClass
        U32(h, 276, page.Dpi); U32(h, 280, page.Dpi);               // HWResolution
        U32(h, 340, Math.Max(1, copies));                           // NumCopies
        U32(h, 352, (int)Math.Round(page.Width * 72.0 / page.Dpi)); // PageSize, points
        U32(h, 356, (int)Math.Round(page.Height * 72.0 / page.Dpi));
        U32(h, 372, page.Width);                                    // Width
        U32(h, 376, page.Height);                                   // Height
        U32(h, 384, 8);                                             // BitsPerColor
        U32(h, 388, 8 * bpp);                                       // BitsPerPixel
        U32(h, 392, page.Width * bpp);                              // BytesPerLine
        U32(h, 396, 0);                                             // ColorOrder: chunky
        U32(h, 400, color ? 19 : 18);                               // ColorSpace: sRGB / sGray
        U32(h, 420, bpp);                                           // NumColors
        U32(h, 452, totalPages);                                    // TotalPageCount
        U32(h, 456, 1); U32(h, 460, 1);                             // CrossFeedTransform, FeedTransform
        s.Write(h);

        // One line in the device byte order: RGB (from our BGR) or a single gray channel.
        var prev = new byte[page.Width * bpp];
        var cur = new byte[page.Width * bpp];
        var output = new MemoryStream(page.Width * bpp + 64);
        int y = 0;
        Line(page, 0, cur, color);
        while (y < page.Height)
        {
            // Count identical following lines (max 256 per group).
            int repeat = 1;
            (prev, cur) = (cur, prev);
            while (y + repeat < page.Height && repeat < 256)
            {
                Line(page, y + repeat, cur, color);
                if (!cur.AsSpan().SequenceEqual(prev)) break;
                repeat++;
            }
            s.WriteByte((byte)(repeat - 1));
            output.SetLength(0);
            Pack(prev, bpp, output);
            output.WriteTo(s);
            y += repeat;
            if (y < page.Height && repeat == 256) Line(page, y, cur, color);   // the run hit the cap: load the next line
        }
    }

    private static void Line(PageImage page, int y, byte[] dst, bool color)
    {
        var src = page.Bgr.AsSpan(y * page.Stride, page.Stride);
        if (color)
        {
            for (int x = 0, d = 0; x < src.Length; x += 3, d += 3)
            {
                dst[d] = src[x + 2]; dst[d + 1] = src[x + 1]; dst[d + 2] = src[x];
            }
        }
        else
        {
            for (int x = 0, d = 0; x < src.Length; x += 3, d++)
                dst[d] = (byte)((src[x + 2] * 77 + src[x + 1] * 150 + src[x] * 29) >> 8);
        }
    }

    /// <summary>Pixel runs: 0..127 = the next pixel repeated n+1 times; 129..255 = 257-n literal pixels follow.</summary>
    private static void Pack(ReadOnlySpan<byte> line, int bpp, Stream o)
    {
        int count = line.Length / bpp, i = 0;
        while (i < count)
        {
            int run = 1;
            while (i + run < count && run < 128 && Same(line, i, i + run, bpp)) run++;
            if (run > 1)
            {
                o.WriteByte((byte)(run - 1));
                o.Write(line.Slice(i * bpp, bpp));
                i += run;
                continue;
            }
            int lit = 1;
            while (i + lit < count && lit < 128 && !(i + lit + 1 < count && Same(line, i + lit, i + lit + 1, bpp))) lit++;
            if (lit == 1) { o.WriteByte(0); o.Write(line.Slice(i * bpp, bpp)); i++; continue; }
            o.WriteByte((byte)(257 - lit));
            o.Write(line.Slice(i * bpp, lit * bpp));
            i += lit;
        }
    }

    private static bool Same(ReadOnlySpan<byte> l, int a, int b, int bpp) => l.Slice(a * bpp, bpp).SequenceEqual(l.Slice(b * bpp, bpp));
    private static void U32(byte[] h, int at, int v) => BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(at), v);
    private static void Ascii(byte[] h, int at, string v) => Encoding.ASCII.GetBytes(v).CopyTo(h, at);
}
