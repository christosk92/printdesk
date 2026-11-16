namespace PrintDesk.Imaging;

/// <summary>How a picture is placed on the sheet.</summary>
enum Placement : byte
{
    /// <summary>As large as the printable area allows, rotated when that makes it bigger.</summary>
    Fit = 0,
    /// <summary>The picture's own physical size (from its DPI), shrunk only when it would not fit.</summary>
    ActualSize = 1,
}

/// <summary>The sheet to render onto.</summary>
readonly record struct PageSpec(double WidthMm, double HeightMm, int Dpi, double MarginMm, bool Color)
{
    public int WidthPx => (int)Math.Round(WidthMm / 25.4 * Dpi);
    public int HeightPx => (int)Math.Round(HeightMm / 25.4 * Dpi);
    public int MarginPx => (int)Math.Round(MarginMm / 25.4 * Dpi);
    public int AreaW => WidthPx - 2 * MarginPx;
    public int AreaH => HeightPx - 2 * MarginPx;
}

/// <summary>One rendered sheet: 24-bit BGR (sRGB), rows packed (stride = Width * 3), white where nothing is drawn.</summary>
sealed class PageImage
{
    public int Width { get; }
    public int Height { get; }
    public int Dpi { get; }
    public byte[] Bgr { get; }
    public int Stride => Width * 3;

    public PageImage(int width, int height, int dpi)
    {
        Width = width; Height = height; Dpi = dpi;
        Bgr = GC.AllocateUninitializedArray<byte>(width * height * 3);
        Bgr.AsSpan().Fill(0xFF);
    }

    /// <summary>Composite premultiplied BGRA pixels over the white sheet at (x, y).</summary>
    public void Blit(ReadOnlySpan<byte> pbgra, int w, int h, int x, int y, bool gray)
    {
        for (int row = 0; row < h; row++)
        {
            int ty = y + row;
            if ((uint)ty >= (uint)Height) continue;
            var src = pbgra.Slice(row * w * 4, w * 4);
            var dst = Bgr.AsSpan(ty * Stride);
            for (int col = 0; col < w; col++)
            {
                int tx = x + col;
                if ((uint)tx >= (uint)Width) continue;
                int s = col * 4, d = tx * 3;
                int inv = 255 - src[s + 3];
                int b = src[s] + inv, g = src[s + 1] + inv, r = src[s + 2] + inv;   // premultiplied over white
                if (gray)
                {
                    int l = (r * 77 + g * 150 + b * 29) >> 8;
                    b = g = r = l;
                }
                dst[d] = (byte)Math.Min(b, 255);
                dst[d + 1] = (byte)Math.Min(g, 255);
                dst[d + 2] = (byte)Math.Min(r, 255);
            }
        }
    }
}
