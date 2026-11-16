using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace PrintDesk.Imaging;

/// <summary>
/// Windows Imaging Component, used directly: decode any installed image format (JPEG, PNG, GIF, BMP, TIFF, HEIC/WebP
/// when their codecs are installed), place it on a sheet, and encode a sheet as JPEG. Every call creates and releases
/// its own COM objects on the calling (worker) thread.
/// </summary>
static unsafe class Wic
{
    private static readonly Guid CLSID_WICImagingFactory = new(0xCACAF262, 0x9370, 0x4615, 0xA1, 0x3B, 0x9F, 0x55, 0x39, 0xDA, 0x4C, 0x0A);
    private static readonly Guid GUID_WICPixelFormat32bppPBGRA = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x10);
    private static readonly Guid GUID_WICPixelFormat24bppBGR = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x0C);
    private static readonly Guid GUID_ContainerFormatJpeg = new(0x19E4A5AA, 0x5662, 0x4FC5, 0xA0, 0xC0, 0x17, 0x58, 0x02, 0x8E, 0x10, 0x57);

    /// <summary>File extensions WIC reads on every Windows install.</summary>
    public static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".heic", ".heif", ".webp", ".jxr", ".wdp", ".avif"];

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static IWICImagingFactory* CreateFactory()
    {
        CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);
        Guid clsid = CLSID_WICImagingFactory;
        Guid iid = __uuidof<IWICImagingFactory>();
        IWICImagingFactory* f = null;
        HRESULT hr = CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&f);
        if (hr.FAILED) throw new PrintException($"Windows Imaging is unavailable (0x{hr.Value:X8}).");
        return f;
    }

    /// <summary>Decode an image file and place it on one sheet.</summary>
    public static PageImage RenderFile(string path, PageSpec page, Placement placement)
    {
        IWICImagingFactory* f = CreateFactory();
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        try
        {
            HRESULT hr;
            fixed (char* p = path)
                hr = f->CreateDecoderFromFilename(p, null, GENERIC_READ, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder);
            if (hr.FAILED) throw new PrintException("Windows can't read this picture (no codec for the format, or the file is damaged).");
            if (decoder->GetFrame(0, &frame).FAILED) throw new PrintException("The picture has no readable frame.");
            double dpiX = 0, dpiY = 0;
            frame->GetResolution(&dpiX, &dpiY);
            return Place(f, (IWICBitmapSource*)frame, ExifTransform(frame), dpiX, page, placement);
        }
        finally
        {
            if (frame != null) frame->Release();
            if (decoder != null) decoder->Release();
            f->Release();
        }
    }

    /// <summary>Place a WIC source on a sheet: orient, fit, scale, composite over white.</summary>
    private static PageImage Place(IWICImagingFactory* f, IWICBitmapSource* source, WICBitmapTransformOptions exif,
        double sourceDpi, PageSpec page, Placement placement)
    {
        IWICFormatConverter* conv = null;
        IWICBitmap* buffered = null;
        IWICBitmapFlipRotator* rotator = null;
        IWICBitmapScaler* scaler = null;
        try
        {
            uint sw = 0, sh = 0;
            if (source->GetSize(&sw, &sh).FAILED || sw == 0 || sh == 0) throw new PrintException("The picture is empty.");

            // Upright first (EXIF), in premultiplied BGRA so transparency composites over the white sheet.
            if (f->CreateFormatConverter(&conv).FAILED) throw new PrintException("Pixel conversion failed.");
            Guid pf = GUID_WICPixelFormat32bppPBGRA;
            if (conv->Initialize(source, &pf, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0.0,
                    WICBitmapPaletteType.WICBitmapPaletteTypeCustom).FAILED)
                throw new PrintException("Pixel conversion failed.");

            bool exifSwap = ((uint)exif & 1u) != 0;
            int w = (int)(exifSwap ? sh : sw), h = (int)(exifSwap ? sw : sh);

            // Turn a landscape picture on a portrait sheet (and vice versa) when fitting: it prints larger.
            bool turn = placement == Placement.Fit && (w > h) != (page.AreaW > page.AreaH) && w != h;
            var transform = exif;
            if (turn) transform = Compose(exif, WICBitmapTransformOptions.WICBitmapTransformRotate90);

            IWICBitmapSource* upright = (IWICBitmapSource*)conv;
            if (transform != WICBitmapTransformOptions.WICBitmapTransformRotate0)
            {
                if (f->CreateBitmapFromSource(upright, WICBitmapCreateCacheOption.WICBitmapCacheOnLoad, &buffered).FAILED
                    || f->CreateBitmapFlipRotator(&rotator).FAILED
                    || rotator->Initialize((IWICBitmapSource*)buffered, transform).FAILED)
                    throw new PrintException("Rotating the picture failed.");
                upright = (IWICBitmapSource*)rotator;
                uint rw = 0, rh = 0;
                rotator->GetSize(&rw, &rh);
                w = (int)rw; h = (int)rh;
            }

            double fit = Math.Min((double)page.AreaW / w, (double)page.AreaH / h);
            double scale = placement == Placement.ActualSize
                ? Math.Min(page.Dpi / (sourceDpi is > 1 and < 5000 ? sourceDpi : 96.0), fit)
                : fit;
            int dw = Math.Max(1, (int)Math.Round(w * scale));
            int dh = Math.Max(1, (int)Math.Round(h * scale));

            // Big enlargements stay crisp (labels, barcodes, screenshots); reductions and small enlargements stay smooth.
            var mode = scale >= 1.5
                ? WICBitmapInterpolationMode.WICBitmapInterpolationModeNearestNeighbor
                : scale >= 1.0
                    ? WICBitmapInterpolationMode.WICBitmapInterpolationModeHighQualityCubic
                    : WICBitmapInterpolationMode.WICBitmapInterpolationModeFant;
            if (f->CreateBitmapScaler(&scaler).FAILED || scaler->Initialize(upright, (uint)dw, (uint)dh, mode).FAILED)
                throw new PrintException("Scaling the picture failed.");

            var sheet = new PageImage(page.WidthPx, page.HeightPx, page.Dpi);
            int x = page.MarginPx + (page.AreaW - dw) / 2;
            int y = placement == Placement.Fit ? page.MarginPx + (page.AreaH - dh) / 2 : page.MarginPx;

            // Pull the scaled pixels in bands so a full-page picture never needs one giant buffer.
            const int band = 256;
            var buf = GC.AllocateUninitializedArray<byte>(dw * 4 * Math.Min(band, dh));
            for (int top = 0; top < dh; top += band)
            {
                int rows = Math.Min(band, dh - top);
                var rect = new WICRect { X = 0, Y = top, Width = dw, Height = rows };
                fixed (byte* d = buf)
                    if (scaler->CopyPixels(&rect, (uint)(dw * 4), (uint)(dw * 4 * rows), d).FAILED)
                        throw new PrintException("Reading the picture's pixels failed.");
                sheet.Blit(buf.AsSpan(0, dw * 4 * rows), dw, rows, x, y + top, !page.Color);
            }
            return sheet;
        }
        finally
        {
            if (scaler != null) scaler->Release();
            if (rotator != null) rotator->Release();
            if (buffered != null) buffered->Release();
            if (conv != null) conv->Release();
        }
    }

    /// <summary>EXIF orientation then an extra clockwise quarter turn.</summary>
    private static WICBitmapTransformOptions Compose(WICBitmapTransformOptions exif, WICBitmapTransformOptions extra)
    {
        uint rot = ((uint)exif & 3u) + ((uint)extra & 3u);
        uint flip = (uint)exif & 0x18u;
        return (WICBitmapTransformOptions)((rot & 3u) | flip);
    }

    private static WICBitmapTransformOptions ExifTransform(IWICBitmapFrameDecode* frame)
    {
        IWICMetadataQueryReader* meta = null;
        if (frame->GetMetadataQueryReader(&meta).FAILED || meta == null) return WICBitmapTransformOptions.WICBitmapTransformRotate0;
        PROPVARIANT pv = default;
        try
        {
            fixed (char* name = "System.Photo.Orientation")
                if (meta->GetMetadataByName(name, &pv).FAILED || pv.vt != (ushort)VARENUM.VT_UI2)
                    return WICBitmapTransformOptions.WICBitmapTransformRotate0;
            return pv.uiVal switch
            {
                2 => WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal,
                3 => WICBitmapTransformOptions.WICBitmapTransformRotate180,
                4 => WICBitmapTransformOptions.WICBitmapTransformFlipVertical,
                5 => WICBitmapTransformOptions.WICBitmapTransformRotate270 | WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal,
                6 => WICBitmapTransformOptions.WICBitmapTransformRotate90,
                7 => WICBitmapTransformOptions.WICBitmapTransformRotate90 | WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal,
                8 => WICBitmapTransformOptions.WICBitmapTransformRotate270,
                _ => WICBitmapTransformOptions.WICBitmapTransformRotate0,
            };
        }
        finally
        {
            PropVariantClear(&pv);
            meta->Release();
        }
    }

    /// <summary>Encode a sheet as a baseline JPEG (what IPP printers take as image/jpeg).</summary>
    public static byte[] EncodeJpeg(PageImage page, float quality = 0.92f)
    {
        IWICImagingFactory* f = CreateFactory();
        IStream* mem = null;
        IWICStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* props = null;
        try
        {
            if (CreateStreamOnHGlobal(HGLOBAL.NULL, TRUE, &mem).FAILED) throw new PrintException("Out of memory.");
            if (f->CreateStream(&stream).FAILED || stream->InitializeFromIStream(mem).FAILED) throw new PrintException("Out of memory.");
            Guid container = GUID_ContainerFormatJpeg;
            if (f->CreateEncoder(&container, null, &encoder).FAILED) throw new PrintException("No JPEG encoder.");
            if (encoder->Initialize((IStream*)stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache).FAILED)
                throw new PrintException("JPEG encoder failed.");
            if (encoder->CreateNewFrame(&frame, &props).FAILED) throw new PrintException("JPEG encoder failed.");

            fixed (char* name = "ImageQuality")
            {
                PROPBAG2 bag = default;
                bag.pstrName = name;
                VARIANT v = default;
                v.vt = (ushort)VARENUM.VT_R4;
                v.fltVal = quality;
                props->Write(1, &bag, &v);
            }
            if (frame->Initialize(props).FAILED) throw new PrintException("JPEG encoder failed.");
            frame->SetSize((uint)page.Width, (uint)page.Height);
            frame->SetResolution(page.Dpi, page.Dpi);
            Guid pf = GUID_WICPixelFormat24bppBGR;
            frame->SetPixelFormat(&pf);
            fixed (byte* px = page.Bgr)
                if (frame->WritePixels((uint)page.Height, (uint)page.Stride, (uint)page.Bgr.Length, px).FAILED)
                    throw new PrintException("JPEG encoder failed.");
            if (frame->Commit().FAILED || encoder->Commit().FAILED) throw new PrintException("JPEG encoder failed.");

            STATSTG stat = default;
            mem->Stat(&stat, (uint)STATFLAG.STATFLAG_NONAME);
            var bytes = new byte[(int)stat.cbSize.QuadPart];
            LARGE_INTEGER zero = default;
            mem->Seek(zero, (uint)STREAM_SEEK.STREAM_SEEK_SET, null);
            uint read = 0;
            fixed (byte* b = bytes) mem->Read(b, (uint)bytes.Length, &read);
            return read == bytes.Length ? bytes : bytes[..(int)read];
        }
        finally
        {
            if (props != null) props->Release();
            if (frame != null) frame->Release();
            if (encoder != null) encoder->Release();
            if (stream != null) stream->Release();
            if (mem != null) mem->Release();
            f->Release();
        }
    }
}

sealed class PrintException(string message) : Exception(message);
